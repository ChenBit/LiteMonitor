using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.src.Core;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.SystemServices;
using LiteMonitor.src.WebServer;
using LibreHardwareMonitor.Hardware;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        // 测试只使用内存配置与模拟硬件，不打开驱动或保存用户设置。
        typeof(Settings).GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, new Settings());
        SettingsHelper.GlobalBlockSave = true;
        await Run("自动模式优先保留主显卡风扇", () => GpuTest("", checkFan: true));
        await Run("失效显卡首选项按自动模式刷新补充传感器", () => GpuTest("不存在的显卡"));
        await Run("同名显卡旧配置按自动模式刷新补充传感器", () => GpuTest("同名显卡", duplicate: true));
        await Run("有效显卡首选项保持严格单卡采集", () => GpuTest("/gpu/primary", explicitSelection: true));
        await Run("关闭 GPU 监控时不预热显卡", GpuDisabledTest);
        await Run("首次更新后才出现的传感器可以建图", GpuDelayedTest);
        foreach (var (name, nominal, percent, registry, expected) in new (string, float?, float?, float, float?)[]
        {
            ("标称频率结合睿频百分比", 3000, 150, 2500, 4500),
            ("标称频率结合降频百分比", 3000, 50, 2500, 1500),
            ("缺少标称计数器时用注册表基准", null, 160, 2500, 4000),
            ("缺少百分比时回退标称值", 3000, null, 2500, 3000),
            ("计数器均缺失时交给硬件传感器", null, null, 2500, null),
            ("拒绝非数值性能百分比", 3000, float.NaN, 2500, null),
            ("拒绝无限性能百分比", 3000, float.PositiveInfinity, 2500, null),
            ("拒绝负性能百分比", 3000, -1, 2500, null),
            ("拒绝零性能百分比", 3000, 0, 2500, null),
            ("拒绝换算后过高频率", 3000, 1000, 2500, null),
            ("拒绝无效基准频率", null, 100, float.NaN, null)
        })
        {
            await Run("CPU 频率：" + name, () =>
            {
                var method = typeof(PerformanceCounterManager).GetMethod("ResolveCpuFrequency", BindingFlags.Static | BindingFlags.NonPublic)!;
                float? actual = (float?)method.Invoke(null, new object?[] { nominal, percent, registry });
                Check(actual == expected, "频率回退计算不符合预期");
                return Task.CompletedTask;
            });
        }
        await Run("特殊字符密码可正常认证且拒绝重复参数", AuthTest);
        await Run("未完整发送的请求头不能进入快照处理", PartialHeaderTest);
        await Run("慢请求头在截止时间后释放连接", HeaderTimeoutTest);
        await Run("超过上限的请求头不能进入快照处理", HeaderSizeTest);
        await Run("股票代码清洗兼容指数与普通美股", StockTest);
        await Run("内存和显存的数值与自定义单位一致", MemoryTest);
        await Run("TLS：插件拒绝不可信服务器证书", async () =>
        {
            using var executor = new PluginExecutor();
            var http = (HttpClient)typeof(PluginExecutor).GetField("_http", Private)!.GetValue(executor)!;
            await RejectUntrustedCertificate(_ => http);
        });
        await Run("TLS：插件代理拒绝不可信服务器证书", async () =>
        {
            using var executor = new PluginExecutor();
            var getClient = typeof(PluginExecutor).GetMethod("GetClient", Private)!;
            await RejectUntrustedCertificate(port => (HttpClient)getClient.Invoke(executor, new object[] { "http://127.0.0.1:" + port })!, proxy: true);
        });
        foreach (var type in new[] { typeof(UpdateChecker), typeof(NetworkSpeedTester) })
        {
            await Run("TLS：" + type.Name + " 拒绝不可信服务器证书", async () =>
            {
                var http = (HttpClient)type.GetField("http", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
                await RejectUntrustedCertificate(_ => http);
            });
        }
        await Run("TLS：原生行情客户端拒绝不可信服务器证书", async () =>
        {
            using var http = (HttpClient)typeof(CryptoNative).GetMethod("CreateClient", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
            await RejectUntrustedCertificate(_ => http);
        });

        Console.WriteLine($"发版审查：通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    private static Task GpuTest(string preference, bool checkFan = false, bool explicitSelection = false, bool duplicate = false)
    {
        var primary = Hardware(HardwareType.GpuNvidia, duplicate ? "同名显卡" : "主显卡", "primary");
        var secondary = Hardware(HardwareType.GpuIntel, duplicate ? "同名显卡" : "Intel UHD", "secondary");
        Proxy(primary).Values["Sensors"] = new[] { Sensor(primary, "GPU Core", SensorType.Load), Sensor(primary, "Fan", SensorType.Fan) };
        Proxy(secondary).Values["Sensors"] = new[] { Sensor(secondary, "GPU Core", SensorType.Temperature), Sensor(secondary, "Fan", SensorType.Fan) };
        var computer = Computer(primary, secondary);
        var cfg = new Settings { PreferredGpu = preference };
        cfg.MonitorItems.Clear();
        cfg.MonitorItems.Add(new MonitorItemConfig { Key = "GPU.Load", VisibleInPanel = true });
        var map = new SensorMap();
        map.Rebuild(computer, cfg);
        if (checkFan)
        {
            Check(map.TryGetSensor("GPU.Fan", out var fan) && ReferenceEquals(fan!.Hardware, primary), "低优先级显卡覆盖了主显卡风扇");
        }
        else
        {
            var monitor = (HardwareMonitor)RuntimeHelpers.GetUninitializedObject(typeof(HardwareMonitor));
            typeof(HardwareMonitor).GetField("_cfg", Private)!.SetValue(monitor, cfg);
            typeof(HardwareMonitor).GetField("_sensorMap", Private)!.SetValue(monitor, map);
            bool refresh = (bool)typeof(HardwareMonitor).GetMethod("ShouldUpdateGpuHardware", Private)!.Invoke(monitor, new object[] { secondary })!;
            Check(refresh == !explicitSelection, "显卡刷新规则与建图选择规则不一致");
            Check(map.TryGetSensor("GPU.Temp", out _) == !explicitSelection, "显卡映射范围不符合预期");
        }
        return Task.CompletedTask;
    }

    private static Task GpuDisabledTest()
    {
        var gpu = Hardware(HardwareType.GpuNvidia, "主显卡", "primary");
        var cfg = new Settings();
        cfg.MonitorItems.Clear();
        new SensorMap().Rebuild(Computer(gpu), cfg);
        Check(Proxy(gpu).Updates == 0, "禁用 GPU 后仍然预热");
        return Task.CompletedTask;
    }

    private static Task GpuDelayedTest()
    {
        var gpu = Hardware(HardwareType.GpuNvidia, "主显卡", "primary");
        Proxy(gpu).OnUpdate = () => Proxy(gpu).Values["Sensors"] = new[] { Sensor(gpu, "GPU Core", SensorType.Load) };
        var cfg = new Settings();
        cfg.MonitorItems.Clear();
        cfg.MonitorItems.Add(new MonitorItemConfig { Key = "GPU.Load", VisibleInPanel = true });
        var map = new SensorMap();
        map.Rebuild(Computer(gpu), cfg);
        Check(Proxy(gpu).Updates == 1 && map.TryGetSensor("GPU.Load", out _), "首次更新产生的传感器没有映射");
        return Task.CompletedTask;
    }

    private static Task AuthTest()
    {
        var manager = new WebSessionManager(_ => { });
        var auth = typeof(WebSessionManager).GetMethod("CheckAuth", Private)!;
        bool Accept(string target) => (bool)auth.Invoke(manager, new object[] { $"GET {target} HTTP/1.1\r\nHost: localhost\r\n\r\n" })!;
        try
        {
            foreach (var password in new[] { "中文空 格+&=%", "a+b", "a b", "plain" })
            {
                Settings.Load().WebServerPassword = password;
                string encoded = Uri.EscapeDataString(password);
                Check(Accept("/api/snapshot?pwd=" + encoded), "编码后的密码认证失败");
                Check(!Accept("/api/snapshot?pwd=wrong"), "错误密码通过认证");
                Check(!Accept("/api/snapshot?pwd=" + encoded + "&pwd=wrong"), "重复密码参数应拒绝");
                Check(!Accept("/api/snapshot?other=pwd=" + encoded), "非独立参数通过认证");
            }
        }
        finally { Settings.Load().WebServerPassword = ""; }
        return Task.CompletedTask;
    }

    private static async Task PartialHeaderTest()
    {
        await WithConnection(async (client, stream, handler) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/snapshot HTTP/1.1\r\nHost: localhost\r\n"));
            client.Client.Shutdown(SocketShutdown.Send);
            byte[] buffer = new byte[1024];
            int count = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            Check(count == 0, "未完整请求头不应得到快照数据");
            await handler.WaitAsync(TimeSpan.FromSeconds(2));
        });
    }

    private static async Task HeaderTimeoutTest()
    {
        await WithConnection(async (_, stream, handler) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
            await handler.WaitAsync(TimeSpan.FromSeconds(7));
            byte[] buffer = new byte[1];
            Check(await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "截止时间后连接未释放");
        });
    }

    private static async Task HeaderSizeTest()
    {
        await WithConnection(async (_, stream, handler) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("GET /api/snapshot HTTP/1.1\r\nPadding: " + new string('a', 8192)));
            await handler.WaitAsync(TimeSpan.FromSeconds(2));
            byte[] buffer = new byte[1];
            try { Check(await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(1)) == 0, "超长请求不应得到快照数据"); }
            catch (IOException) { }
        });
    }

    private static async Task WithConnection(Func<TcpClient, NetworkStream, Task, Task> test)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var accepted = await listener.AcceptTcpClientAsync();
        var manager = new WebSessionManager(w => { w.WriteStartObject(); w.WriteEndObject(); });
        var handler = manager.HandleClientAsync(accepted);
        try { await test(client, client.GetStream(), handler); }
        finally
        {
            client.Close();
            listener.Stop();
            manager.Stop();
            await handler.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static Task StockTest()
    {
        var template = JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Stock.json")))!;
        foreach (var (input, expected) in new[] { ("us.NDX", "usNDX"), ("usBABA.N", "usBABA"), ("usAAPL.O", "usAAPL"), ("sh600519", "sh600519") })
        {
            var context = new Dictionary<string, string> { ["code"] = input };
            PluginProcessor.ApplyTransforms(template.Execution.Steps![0].Process!, context);
            Check(context["code"] == expected, "股票代码清洗错误：" + input);
        }
        return Task.CompletedTask;
    }

    private static async Task RejectUntrustedCertificate(Func<int, HttpClient> getClient, bool proxy = false)
    {
        // 只在本地生成临时自签名证书，不修改系统信任存储，也不依赖外部测试网站。
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows 的 TLS 服务端需要导入带私钥的证书；仅导入内存对象，不加入信任存储。
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            var network = socket.GetStream();
            if (proxy)
            {
                var header = new StringBuilder();
                var one = new byte[1];
                while (!header.ToString().EndsWith("\r\n\r\n"))
                {
                    int count = await network.ReadAsync(one, timeout.Token);
                    if (count == 0 || header.Length > 8192) throw new IOException("代理请求不完整");
                    header.Append((char)one[0]);
                }
                Check(header.ToString().StartsWith("CONNECT "), "应通过显式代理建立隧道");
                await network.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), timeout.Token);
            }
            using var ssl = new SslStream(network);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
                var buffer = new byte[4096];
                int count = await ssl.ReadAsync(buffer, timeout.Token);
                if (count > 0)
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), timeout.Token);
            }
            catch (AuthenticationException) { }
            catch (IOException) { }
        });
        try
        {
            var http = getClient(port);
            bool rejected = false;
            try { await http.GetStringAsync(proxy ? "https://localhost/" : $"https://127.0.0.1:{port}/", timeout.Token); }
            catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException) { rejected = true; }
            Check(rejected, "不可信证书被接受，或请求未到达证书校验阶段");
        }
        finally
        {
            timeout.Cancel();
            listener.Stop();
            try { await serve; } catch (OperationCanceledException) { }
        }
    }

    private static Task MemoryTest()
    {
        var cfg = Settings.Load();
        cfg.MemoryDisplayMode = 1;
        Settings.DetectedRamTotalGB = 32;
        Settings.DetectedGpuVramTotalGB = 8;
        foreach (string key in new[] { "MEM.Load", "GPU.VRAM" })
        {
            string expectedCapacity = key == "MEM.Load" ? "16.0" : "4.00";
            Check(MetricUtils.GetValueStr(key, 50, false, "%") == "50.0", "百分号单位未保留百分比数值");
            Check(MetricUtils.GetValueStr(key, 50, false, "{u}") == expectedCapacity, "默认容量转换错误");
            var item = new MetricItem { Key = key, DisplayValue = 50, BoundConfig = new MonitorItemConfig { Key = key, UnitPanel = "%", UnitTaskbar = "{u}" } };
            Check(item.GetFormattedText(false) == "50.0%", "面板格式化不一致");
            Check(item.GetFormattedText(true) == expectedCapacity + "GB", "任务栏格式化不一致");
        }
        return Task.CompletedTask;
    }

    private static IHardware Hardware(HardwareType type, string name, string id)
    {
        var hardware = DispatchProxy.Create<IHardware, StubProxy>();
        Proxy(hardware).Values = new() { ["HardwareType"] = type, ["Name"] = name, ["Identifier"] = new Identifier("gpu", id), ["Sensors"] = Array.Empty<ISensor>(), ["SubHardware"] = Array.Empty<IHardware>() };
        return hardware;
    }

    private static ISensor Sensor(IHardware hardware, string name, SensorType type)
    {
        var sensor = DispatchProxy.Create<ISensor, StubProxy>();
        Proxy(sensor).Values = new() { ["Hardware"] = hardware, ["Name"] = name, ["SensorType"] = type, ["Value"] = 50f };
        return sensor;
    }

    private static Computer Computer(params IHardware[] hardware)
    {
        var computer = new Computer();
        var field = typeof(Computer).GetField("_groups", Private)!;
        var groupType = field.FieldType.GenericTypeArguments[0];
        object group = DispatchProxy.Create(groupType, typeof(StubProxy));
        Proxy(group).Values["Hardware"] = hardware;
        ((IList)field.GetValue(computer)!).Add(group);
        return computer;
    }

    private static StubProxy Proxy(object value) => (StubProxy)value;
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("通过：" + name); }
        catch (Exception ex) { _failed++; Console.WriteLine($"失败：{name}：{ex.GetBaseException().Message}"); }
    }
}

// 用动态代理模拟硬件接口，只向真实业务代码提供测试所需的传感器。
public class StubProxy : DispatchProxy
{
    public Dictionary<string, object?> Values = new();
    public Action? OnUpdate;
    public int Updates;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "Update") { Updates++; OnUpdate?.Invoke(); return null; }
        if (method.Name.StartsWith("get_")) return Values.GetValueOrDefault(method.Name[4..]);
        return null;
    }
}
