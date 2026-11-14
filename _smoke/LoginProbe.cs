using System;
using System.IO;
using System.Threading.Tasks;
using GeZi.Core.Api;
using GeZi.Providers.Quark;
using GeZi.Providers.Qr;
using GeZi.Providers.Shared;
using GeZi.Core.Support;

internal static class LoginProbe
{
    /// 读取已保存的账号档案，验证 Cookie 是否真的有效
    public static async Task RunAsync()
    {
        Console.WriteLine();
        Console.WriteLine("[8] 已保存账号的登录态验证");

        string dir = Environment.GetEnvironmentVariable("GEZI_CONFIG_DIR")
                     ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "GeZi");
        string accDir = Path.Combine(dir, "accounts");
        Console.WriteLine("    档案目录 = " + accDir);

        if (!Directory.Exists(accDir)) { Console.WriteLine("    (无账号档案)"); return; }

        foreach (var f in Directory.GetFiles(accDir, "*.json"))
        {
            Console.WriteLine("    --- " + Path.GetFileName(f));
            try
            {
                var p = System.Text.Json.JsonSerializer.Deserialize<Profile>(File.ReadAllText(f));
                Console.WriteLine("        Key  = " + p.Key);
                Console.WriteLine("        Nick = " + p.Nick);

                string cookie = SecretProtector.Unprotect(p.Cookie);
                Console.WriteLine("        Cookie 解密 = " + (string.IsNullOrEmpty(cookie)
                    ? "✗ 失败（密钥不匹配？）"
                    : "✓ " + cookie.Length + " 字符"));

                if (string.IsNullOrEmpty(cookie)) continue;

                // 看关键登录 Cookie 在不在
                var kv = QuarkSession.ParseCookies(cookie);
                foreach (var k in new[] { "__pus", "__puus", "__kp", "_UP_A4A_11_", "b-user-id" })
                    Console.WriteLine("        " + k.PadRight(12) + " = " + (kv.ContainsKey(k) ? "✓" : "✗ 缺失"));

                // 真正调一次账号接口
                using (var client = new QuarkClient(cookie))
                {
                    var (ok, nick) = await client.ValidateAsync();
                    Console.WriteLine("        ValidateAsync = " + (ok ? "✓ 有效" : "✗ 无效") + " / nick=" + (nick ?? "(空)"));

                    var (total, used) = await client.GetCapacityAsync();
                    Console.WriteLine("        容量 = " + (total > 0
                        ? string.Format("{0:F2} GB / 已用 {1:F2} GB", total / 1e9, used / 1e9)
                        : "✗ 查询失败（多半是 Cookie 无效）"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("        ✗ 异常: " + ex.GetType().Name + " " + ex.Message);
            }
        }
    }

    private sealed class Profile
    {
        public string Key { get; set; }
        public string Nick { get; set; }
        public string Cookie { get; set; }
    }
}
