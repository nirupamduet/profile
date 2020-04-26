using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using MimeKit;
using MimeKit.Utils;
using Nop.Core.Domain.Messages;
using Nop.Services.Media;

namespace Nop.Services.Messages
{
    /// <summary>
    /// Email sender
    /// Referemce documentation http://www.mimekit.net/docs/html/Creating-Messages.htm
    /// </summary>
    public partial class AwsSesEmailSender : IEmailSender
    {
        private readonly IDownloadService _downloadService;
        private readonly EmailAccountSettings _emailAccountSettings;
        private readonly Logging.ILogger _logger;

        /// <summary>
        /// Ctor
        /// </summary>
        /// <param name="downloadService">Download service</param>
        public AwsSesEmailSender(IDownloadService downloadService, EmailAccountSettings emailAccountSettings, Logging.ILogger logger)
        {
            this._downloadService = downloadService;
            this._emailAccountSettings = emailAccountSettings;
            this._logger = logger;
        }

        #region Sends an email (AWS SES API, attachment will be linked in body as html anchor(<a href='link'>link</a>) element) 
                
        /// <summary>
        /// Sends an email
        /// </summary>
        /// <param name="emailAccount">Email account to use</param>
        /// <param name="subject">Subject</param>
        /// <param name="body">Body</param>
        /// <param name="fromAddress">From address</param>
        /// <param name="fromName">From display name</param>
        /// <param name="toAddress">To address</param>
        /// <param name="toName">To display name</param>
        /// <param name="replyTo">ReplyTo address</param>
        /// <param name="replyToName">ReplyTo display name</param>
        /// <param name="bcc">BCC addresses list</param>
        /// <param name="cc">CC addresses list</param>
        /// <param name="attachmentFilePath">Attachment file path</param>
        /// <param name="attachmentFileName">Attachment file name. If specified, then this file name will be sent to a recipient. Otherwise, "AttachmentFilePath" name will be used.</param>
        /// <param name="attachedDownloadId">Attachment download ID (another attachedment)</param>
        /// <param name="headers">Headers</param>
        public virtual void SendEmail(EmailAccount emailAccount, string subject, string body,
            string fromAddress, string fromName, string toAddress, string toName,
            string replyTo = null, string replyToName = null,
            IEnumerable<string> bcc = null, IEnumerable<string> cc = null,
            string attachmentFilePath = null, string attachmentFileName = null,
            int attachedDownloadId = 0, IDictionary<string, string> headers = null)
        {
            //attachment support (v2.0 with attachment)
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, fromAddress));
            message.To.Add(new MailboxAddress(toName, toAddress));

            //reply to
            if (!string.IsNullOrEmpty(replyTo))
            {
                message.ReplyTo.Add(new MailboxAddress(replyTo));
            }

            //BCC
            if (bcc != null)
            {
                foreach (var address in bcc.Where(bccValue => !string.IsNullOrWhiteSpace(bccValue)))
                {
                    message.Bcc.Add(new MailboxAddress(address.Trim()));
                }
            }

            //CC
            if (cc != null)
            {
                foreach (var address in cc.Where(ccValue => !string.IsNullOrWhiteSpace(ccValue)))
                {
                    message.Cc.Add(new MailboxAddress(address.Trim()));
                }
            }
            
              if (headers != null)
                foreach (var header in headers)
                {
                    message.Headers.Add(new Header(header.Key, header.Value));
                }                        

            // Set the plain-text/html version of the message text
            var builder = new BodyBuilder
            {
                HtmlBody = body,                
            };

            //build message
            message.Subject = subject;

            //create the file attachment for this e-mail message
            if (!string.IsNullOrEmpty(attachmentFilePath) &&
                File.Exists(attachmentFilePath))
            {
                // create a linked resource for attachment
                var resources = builder.LinkedResources.Add(attachmentFilePath);
                resources.ContentId = MimeUtils.GenerateMessageId();

                //add Attachments in body
                //builder.HtmlBody += $"<a href=\"{attachmentFilePath}\">{attachmentFileName}</a>";
            }

            //another attachment?
            if (attachedDownloadId > 0)
            {
                var download = _downloadService.GetDownloadById(attachedDownloadId);
                if (download != null)
                {
                    //we do not support URLs as attachments
                    if (!download.UseDownloadUrl)
                    {
                        var downloadFileName = !string.IsNullOrWhiteSpace(download.Filename) ? download.Filename : download.Id.ToString();
                        downloadFileName += download.Extension;
                        
                        // create a linked resource for attachment
                        var resources = builder.LinkedResources.Add(downloadFileName,download.DownloadBinary,ContentType.Parse(download.ContentType));
                        resources.ContentId = MimeUtils.GenerateMessageId();

                        //add Attachments in body
                        //builder.HtmlBody += $"<a href=\"{downloadFileName}\">{downloadFileName}</a>";
                    }
                }
            }

            //build message
            message.Body = builder.ToMessageBody();

            //send email
            using (var client = new AmazonSimpleEmailServiceClient(_emailAccountSettings.AwsAccessId, _emailAccountSettings.AwsAccessKey, RegionEndpoint.USEast1))
            {
                try
                {
                    var stream = new MemoryStream();
                    message.WriteTo(stream);
                    var response = client.SendRawEmail(new SendRawEmailRequest()
                    {
                        RawMessage = new RawMessage()
                        {
                            Data = stream
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.InsertLog(Core.Domain.Logging.LogLevel.Debug, "Error message: " + ex.Message);
                }
            }
        }

        #endregion
    }
}
