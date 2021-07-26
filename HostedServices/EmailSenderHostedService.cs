using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Data;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MailKit.Net.Smtp;
using SequentialGuid;
using BackgroundEmailSenderSample.Models.Options;
using BackgroundEmailSenderSample.Models.Services.Infrastructure;
using BackgroundEmailSenderSample.Models.Enums;

namespace BackgroundEmailSenderSample.HostedServices
{
    public class EmailSenderHostedService : IEmailSender, IHostedService, IDisposable
    {
        private readonly BufferBlock<MimeMessage> mailMessages;
        private readonly ILogger logger;
        private readonly IOptionsMonitor<SmtpOptions> optionsMonitor;
        private readonly IDatabaseAccessor db;
        private CancellationTokenSource deliveryCancellationTokenSource;
        private Task deliveryTask;

        public EmailSenderHostedService(IConfiguration configuration, IDatabaseAccessor db, IOptionsMonitor<SmtpOptions> optionsMonitor, ILogger<EmailSenderHostedService> logger)
        {
            this.optionsMonitor = optionsMonitor;
            this.logger = logger;
            this.mailMessages = new BufferBlock<MimeMessage>();
            this.db = db;
        }

        public async Task SendEmailAsync(string email, string subject, string htmlMessage)
        {
            var message = CreateMessage(email, subject, htmlMessage);

            int affectedRows = await db.CommandAsync($@"INSERT INTO EmailMessages (Id, Recipient, Subject, Message, SenderCount, Status) 
                                                        VALUES ({message.MessageId}, {email}, {subject}, {htmlMessage}, 0, {nameof(MailStatus.InProgress)})");

            if (affectedRows != 1)
            {
                throw new InvalidOperationException($"Could not persist email message to {email}");
            }

            await this.mailMessages.SendAsync(message);
        }

        public async Task StartAsync(CancellationToken token)
        {
            logger.LogInformation("Starting background e-mail delivery");

            FormattableString query = $@"SELECT Id, Recipient, Subject, Message FROM EmailMessages WHERE Status NOT IN ({nameof(MailStatus.Sent)}, {nameof(MailStatus.Deleted)})";
            DataSet dataSet = await db.QueryAsync(query);

            try
            {
                foreach (DataRow row in dataSet.Tables[0].Rows)
                {
                    var message = CreateMessage(Convert.ToString(row["Recipient"]),
                                                Convert.ToString(row["Subject"]),
                                                Convert.ToString(row["Message"]),
                                                Convert.ToString(row["Id"]));

                    await this.mailMessages.SendAsync(message, token);
                }

                logger.LogInformation("Email delivery started: {count} message(s) were resumed for delivery", dataSet.Tables[0].Rows.Count);

                deliveryCancellationTokenSource = new CancellationTokenSource();
                deliveryTask = DeliverAsync(deliveryCancellationTokenSource.Token);
            }
            catch (Exception startException)
            {
                logger.LogError(startException, "Couldn't start email delivery");
            }
        }

        public async Task StopAsync(CancellationToken token)
        {
            CancelDeliveryTask();
            // Wait for the send task to stop gracefully. If it takes too much, then we stop waiting
            // as soon as the application cancels the token (i.e when it signals it's not willing to wait any longer)
            await Task.WhenAny(deliveryTask, Task.Delay(Timeout.Infinite, token));
        }

        private void CancelDeliveryTask()
        {
            try
            {
                if (deliveryCancellationTokenSource != null)
                {
                    logger.LogInformation("Stopping e-mail background delivery");
                    deliveryCancellationTokenSource.Cancel();
                    deliveryCancellationTokenSource = null;
                }
            }
            catch
            {
            }
        }

        public async Task DeliverAsync(CancellationToken token)
        {
            logger.LogInformation("E-mail background delivery started");
            while (!token.IsCancellationRequested)
            {
                MimeMessage message = null;
                try
                {
                    message = await mailMessages.ReceiveAsync(token);

                    var options = this.optionsMonitor.CurrentValue;
                    using var client = new SmtpClient();

                    await client.ConnectAsync(options.Host, options.Port, options.Security, token);
                    if (!string.IsNullOrEmpty(options.Username))
                    {
                        await client.AuthenticateAsync(options.Username, options.Password, token);
                    }

                    await client.SendAsync(message, token);
                    await client.DisconnectAsync(true, token);

                    await db.CommandAsync($"UPDATE EmailMessages SET Status={nameof(MailStatus.Sent)} WHERE Id={message.MessageId}", token);
                    logger.LogInformation($"E-mail sent successfully to {message.To}");
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception sendException)
                {
                    var recipient = message?.To[0];
                    logger.LogError(sendException, "Couldn't send an e-mail to {recipient}", recipient);

                    // Increment the sender count
                    try
                    {
                        bool shouldRequeue = await db.QueryScalarAsync<bool>($"UPDATE EmailMessages SET SenderCount = SenderCount + 1, Status=CASE WHEN SenderCount < {optionsMonitor.CurrentValue.MaxSenderCount} THEN Status ELSE {nameof(MailStatus.Deleted)} END WHERE Id={message.MessageId}; SELECT COUNT(*) FROM EmailMessages WHERE Id={message.MessageId} AND Status NOT IN ({nameof(MailStatus.Deleted)}, {nameof(MailStatus.Sent)})", token);
                        if (shouldRequeue)
                        {
                            await mailMessages.SendAsync(message, token);
                        }
                    }
                    catch (Exception requeueException)
                    {
                        logger.LogError(requeueException, "Couldn't requeue message to {0}", recipient);
                    }

                    // An unexpected error occurred during delivery, so we wait before moving on
                    await Task.Delay(optionsMonitor.CurrentValue.DelayOnError, token);
                }
            }

            logger.LogInformation("E-mail background delivery stopped");
        }

        public void Dispose()
        {
            CancelDeliveryTask();
        }

        private MimeMessage CreateMessage(string email, string subject, string htmlMessage, string messageId = null)
        {
            var message = new MimeMessage();

            message.From.Add(MailboxAddress.Parse(optionsMonitor.CurrentValue.Sender));
            message.To.Add(MailboxAddress.Parse(email));
            message.Subject = subject;

            // Se il messageId non è stato fornito, allora lo genero.
            message.MessageId = messageId ?? SequentialGuidGenerator.Instance.NewGuid().ToString();
            message.Body = new TextPart("html") { Text = htmlMessage };

            return message;
        }
    }
}