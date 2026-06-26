using System;

namespace GoBurhan.Helpers
{
    public static class UserAgentParser
    {
        public static (string Browser, string OS) Parse(string userAgent)
        {
            if (string.IsNullOrWhiteSpace(userAgent))
            {
                return ("Unknown", "Unknown");
            }

            var uaLower = userAgent.ToLowerInvariant();

            // 1. Parse Operating System
            string os = "Unknown";
            if (uaLower.Contains("windows"))
            {
                os = "Windows";
            }
            else if (uaLower.Contains("android"))
            {
                os = "Android";
            }
            else if (uaLower.Contains("iphone") || uaLower.Contains("ipad") || uaLower.Contains("ipod"))
            {
                os = "iOS";
            }
            else if (uaLower.Contains("mac os x") || uaLower.Contains("macintosh"))
            {
                os = "macOS";
            }
            else if (uaLower.Contains("linux"))
            {
                os = "Linux";
            }

            // 2. Parse Browser
            string browser = "Unknown";
            if (uaLower.Contains("edg/"))
            {
                browser = "Edge";
            }
            else if (uaLower.Contains("opr/") || uaLower.Contains("opera"))
            {
                browser = "Opera";
            }
            else if (uaLower.Contains("chrome") && !uaLower.Contains("chromium"))
            {
                browser = "Chrome";
            }
            else if (uaLower.Contains("safari") && !uaLower.Contains("chrome") && !uaLower.Contains("android"))
            {
                browser = "Safari";
            }
            else if (uaLower.Contains("firefox"))
            {
                browser = "Firefox";
            }
            else if (uaLower.Contains("msie") || uaLower.Contains("trident"))
            {
                browser = "Internet Explorer";
            }

            return (browser, os);
        }
    }
}
