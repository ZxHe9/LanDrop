using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LanDrop
{
    public static class WebAssets
    {
        private static readonly object Sync = new object();
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        public static byte[] Get(string path, out string contentType)
        {
            contentType = "application/octet-stream";
            string resource;
            switch (path)
            {
                case "/":
                case "/index.html":
                    resource = "Web.index.html";
                    contentType = "text/html; charset=utf-8";
                    break;
                case "/app.js":
                    resource = "Web.app.js";
                    contentType = "application/javascript; charset=utf-8";
                    break;
                case "/style.css":
                    resource = "Web.style.css";
                    contentType = "text/css; charset=utf-8";
                    break;
                case "/logo.png":
                    resource = "Web.logo.png";
                    contentType = "image/png";
                    break;
                case "/favicon.ico":
                    resource = "Web.logo.png";
                    contentType = "image/png";
                    break;
                default:
                    return null;
            }

            lock (Sync)
            {
                byte[] data;
                if (Cache.TryGetValue(resource, out data)) return data;
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream(resource))
                {
                    if (stream == null)
                    {
                        Log.Warn("缺少嵌入资源：" + resource);
                        return null;
                    }
                    using (MemoryStream ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        data = ms.ToArray();
                    }
                }
                Cache[resource] = data;
                return data;
            }
        }
    }
}
