using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PotPlayerAiSubtitle
{
    internal static class CredentialStore
    {
        private const string TargetName = "PotPlayer.AISubtitle.DeepSeekApiKey";
        private const uint CredTypeGeneric = 1;
        private const uint CredPersistLocalMachine = 2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern void CredFree(IntPtr buffer);

        public static string ReadApiKey()
        {
            IntPtr pointer;
            if (!CredRead(TargetName, CredTypeGeneric, 0, out pointer)) return null;
            try
            {
                NativeCredential credential = (NativeCredential)Marshal.PtrToStructure(pointer, typeof(NativeCredential));
                if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
                return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
            }
            finally
            {
                CredFree(pointer);
            }
        }

        public static void SaveApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("API Key 不能为空。");
            string value = apiKey.Trim();
            IntPtr blob = Marshal.StringToCoTaskMemUni(value);
            try
            {
                NativeCredential credential = new NativeCredential();
                credential.Type = CredTypeGeneric;
                credential.TargetName = TargetName;
                credential.Comment = "魔芋字幕工具使用的 API Key";
                credential.CredentialBlobSize = (uint)(value.Length * 2);
                credential.CredentialBlob = blob;
                credential.Persist = CredPersistLocalMachine;
                credential.UserName = "AI Subtitle API";
                if (!CredWrite(ref credential, 0))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeCoTaskMem(blob);
            }
        }
    }

    internal static class ApiEndpoint
    {
        public static string ChatCompletions(string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("API 请求地址不能为空。");
            string value = baseUrl.Trim().TrimEnd('/');
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("API 请求地址格式不正确，请填写 http 或 https 地址。");
            if (value.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return value;
            return value + "/chat/completions";
        }
    }

    internal static class ModelConnectionTester
    {
        public static string Test(AppConfig config, string apiKey, CancellationToken cancellation)
        {
            if (config == null) throw new ArgumentNullException("config");
            if (string.IsNullOrWhiteSpace(config.Model)) throw new ArgumentException("模型名称不能为空。");
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("请先填写或保存 API Key。");

            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Dictionary<string, object> payload = new Dictionary<string, object>();
            payload["model"] = config.Model.Trim();
            payload["max_tokens"] = 4;
            payload["stream"] = false;
            payload["messages"] = new object[]
            {
                new Dictionary<string, object>
                {
                    { "role", "user" },
                    { "content", "Reply only with OK." }
                }
            };

            byte[] body = Encoding.UTF8.GetBytes(AtomicJson.Serialize(payload));
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(ApiEndpoint.ChatCompletions(config.ApiBaseUrl));
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Accept = "application/json";
            request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey.Trim();
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.ContentLength = body.Length;

            using (cancellation.Register(delegate { try { request.Abort(); } catch { } }))
            {
                try
                {
                    using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                    using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                    using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    {
                        reader.ReadToEnd();
                        return "连接成功（HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + "）";
                    }
                }
                catch (WebException ex)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException();
                    string detail = ex.Message;
                    HttpWebResponse response = ex.Response as HttpWebResponse;
                    if (response != null)
                    {
                        try
                        {
                            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                            {
                                string text = reader.ReadToEnd();
                                if (text.Length > 500) text = text.Substring(0, 500);
                                detail = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + " " + text;
                            }
                        }
                        catch { }
                    }
                    throw new InvalidOperationException("连接测试失败：" + detail, ex);
                }
            }
        }
    }
}