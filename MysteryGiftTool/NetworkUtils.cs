using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;

using MysteryGiftTool.Properties;

namespace MysteryGiftTool
{
    public sealed class RequestResult
    {
        public bool Success;
        public byte[] Data;
        public HttpStatusCode Status;
        public string Error;

        public string Text => Data == null ? null : Encoding.UTF8.GetString(Data);
    }

    public static class NetworkUtils
    {
        public const int MaxAttempts = 4;
        public const int RetryDelayMs = 2000;   // multiplied by attempt number
        public const int RequestDelayMs = 250;  // politeness delay between successful downloads

        private static X509Certificate2 cert;
        private static X509Certificate2 ClCertA =>
            cert ?? (cert = new X509Certificate2(Resources.ClCertA, Resources.ClCertA_Password));

        /// <summary>
        /// Single request. Always sends ClCertA and the CTR user agent - the original code
        /// only did this for the file list, and used a bare WebClient for the archives.
        /// </summary>
        public static RequestResult Request(string url, bool json = false)
        {
            var result = new RequestResult();

            HttpWebRequest wr;
            try
            {
                wr = (HttpWebRequest)WebRequest.Create(new Uri(url));
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            // InvariantCulture matters: on a non-English Windows install, "MMMM" produces a
            // localised month name and the server sees a user agent no 3DS would ever send.
            wr.UserAgent = "CTR NUP 040600 " +
                DateTime.Now.ToString("MMMM dd yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            wr.KeepAlive = true;
            wr.Method = WebRequestMethods.Http.Get;
            wr.Timeout = 30000;
            wr.ReadWriteTimeout = 60000;
            if (json)
                wr.Accept = "application/json";
            wr.ClientCertificates.Clear();
            wr.ClientCertificates.Add(ClCertA);

            try
            {
                using (var resp = (HttpWebResponse)wr.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    result.Status = resp.StatusCode;
                    stream?.CopyTo(ms);
                    result.Data = ms.ToArray();
                    result.Success = (int)resp.StatusCode < 400;
                    if (!result.Success)
                        result.Error = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                }
            }
            catch (WebException ex)
            {
                // ex.Response is null for DNS failures, connection resets and timeouts.
                // The original code dereferenced it unconditionally.
                var resp = ex.Response as HttpWebResponse;
                if (resp == null)
                {
                    result.Error = $"{ex.Status}: {ex.Message}";
                    return result;
                }

                using (resp)
                using (var stream = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    result.Status = resp.StatusCode;
                    stream?.CopyTo(ms);
                    result.Data = ms.ToArray();
                    result.Error = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
            }

            return result;
        }

        /// <summary>
        /// Retries transient failures with linear backoff. 404 is treated as permanent.
        /// </summary>
        public static RequestResult RequestWithRetry(string url, bool json = false)
        {
            RequestResult last = null;

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                last = Request(url, json);
                if (last.Success)
                    return last;
                if (last.Status == HttpStatusCode.NotFound)
                    return last;

                if (attempt < MaxAttempts)
                {
                    Program.Log($"  attempt {attempt}/{MaxAttempts} failed ({last.Error ?? "unknown error"}); retrying...");
                    Thread.Sleep(RetryDelayMs * attempt);
                }
            }

            return last;
        }
    }
}
