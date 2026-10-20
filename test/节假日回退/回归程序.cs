using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LiteMonitor;
using LiteMonitor.src.Plugins;
using LiteMonitor.src.Plugins.Native;
using LiteMonitor.src.SystemServices.InfoService;

internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--live"))
        {
            await Run("真实接口完整执行链", async () =>
            {
                using var executor = new PluginExecutor();
                var instance = Instance();
                Check(await executor.ExecuteInstanceAsync(instance, LoadTemplate()), "实际插件执行应成功");
                string value = InfoService.Instance.GetValue(instance.Id + ".next");
                string label = InfoService.Instance.GetValue("PROP.Label.DASH." + instance.Id + ".next");
                Check(int.TryParse(value, out int days) && days >= 0, "实际显示应为非负天数");
                Check(!string.IsNullOrEmpty(label) && !label.Contains(PluginConstants.STATUS_UNKNOWN), "实际显示名称应有效");
                Console.WriteLine($"实际输出：{label} {value} 天");
            });
            return _failed == 0 ? 0 : 1;
        }

        await Run("原始空数据能复现执行链中断", async () =>
        {
            var template = LoadTemplate();
            template.Execution.Steps![0].ContinueOnError = false;
            await CheckExecution(template, _ => Json("{\"code\":0,\"holiday\":null}"), false);
        });

        foreach (var (name, response) in new[]
        {
            ("中间节点为空", "{\"code\":0,\"holiday\":null,\"workday\":null}"),
            ("路径缺失", "{\"code\":0}"),
            ("部分字段缺失", "{\"holiday\":{\"name\":\"春节\",\"rest\":7}}"),
            ("天数为空", "{\"holiday\":{\"name\":\"春节\",\"date\":\"2027-02-06\",\"rest\":null}}"),
            ("错误响应", "{\"code\":-1,\"errmsg\":\"无数据\"}"),
            ("无效 JSON", "{invalid"),
            ("网页响应", "<html>错误页面</html>")
        })
        {
            await Run(name + "时回退元旦", () => CheckExecution(LoadTemplate(), _ => Json(response), true));
        }

        await Run("HTTP 失败时回退元旦", () => CheckExecution(LoadTemplate(),
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)), true));
        await Run("网络异常时回退元旦", () => CheckExecution(LoadTemplate(),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("模拟断网")), true));
        await Run("请求超时时回退元旦", () => CheckExecution(LoadTemplate(), async token =>
        {
            await Task.Delay(5000, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, true, timeout: TimeSpan.FromMilliseconds(50)));

        foreach (int rest in new[] { 3, 0 })
        {
            await Run($"主源天数为 {rest} 时跳过备用步骤", async () =>
            {
                var template = LoadTemplate();
                // 如果误执行备用步骤，未知原生地址会使整个执行链失败。
                template.Execution.Steps![1].Url = "native://must-not-run";
                await CheckExecution(template, _ => Json(JsonSerializer.Serialize(new { holiday = new { name = "国庆节", date = "2026-10-01", rest } })),
                    true, rest.ToString(), "距离国庆节");
            });
        }

        await Run("失败步骤的提取和转换不污染原有变量", RollbackTest);
        await Run("用户取消不触发回退或覆盖已有显示", CancellationTest);
        await Run("请求失败与用户取消同时发生时不覆盖已有显示", CancellationFailureTest);
        await Run("失败响应不缓存且下次刷新恢复主源", RecoveryTest);
        await Run("网页错误响应不缓存且下次刷新恢复主源", () => RecoveryTest("<html>临时错误</html>"));
        await Run("未开启回退时拒绝网页错误响应", async () =>
        {
            var template = LoadTemplate();
            template.Execution.Steps![0].ContinueOnError = false;
            await CheckExecution(template, _ => Json("<html>错误</html>"), false);
        });
        await Run("原有插件默认不忽略错误", () =>
        {
            var step = JsonSerializer.Deserialize<PluginExecutionStep>("{\"id\":\"旧插件\"}")!;
            Check(!step.ContinueOnError, "未声明回退时必须维持原有行为");
            return Task.CompletedTask;
        });

        foreach (var (today, date, rest) in new[]
        {
            (new DateTime(2026, 10, 20), "2027-01-01", 73),
            (new DateTime(2026, 10, 21), "2027-01-01", 72),
            (new DateTime(2026, 12, 31, 23, 59, 59), "2027-01-01", 1),
            (new DateTime(2027, 1, 1, 12, 0, 0), "2027-01-01", 0),
            (new DateTime(2027, 1, 2), "2028-01-01", 364),
            (new DateTime(2024, 1, 2), "2025-01-01", 365),
            (new DateTime(2024, 2, 29), "2025-01-01", 307)
        })
        {
            await Run($"日期边界 {today:yyyy-MM-dd HH:mm:ss}", () =>
            {
                CheckCountdown(today, date, rest);
                return Task.CompletedTask;
            });
        }

        await Run("非公历区域设置仍输出公历日期", () =>
        {
            var original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
                CheckCountdown(new DateTime(2026, 10, 21), "2027-01-01", 72);
            }
            finally { CultureInfo.CurrentCulture = original; }
            return Task.CompletedTask;
        });

        Console.WriteLine($"总计通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    private static PluginTemplate LoadTemplate() =>
        JsonSerializer.Deserialize<PluginTemplate>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Holiday.json")))!;

    private static PluginInstanceConfig Instance() => new() { Id = "回归_" + Guid.NewGuid().ToString("N"), TemplateId = "Holiday" };

    private static PluginExecutor Executor(Func<CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null)
    {
        var executor = new PluginExecutor();
        // 注入内存 HTTP 响应，验证真实执行引擎且不依赖外网服务。
        var field = typeof(PluginExecutor).GetField("_http", BindingFlags.Instance | BindingFlags.NonPublic)!;
        ((HttpClient)field.GetValue(executor)!).Dispose();
        field.SetValue(executor, new HttpClient(new Handler(respond)) { Timeout = timeout ?? TimeSpan.FromSeconds(2) });
        return executor;
    }

    private static Task<HttpResponseMessage> Json(string body) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    });

    private static async Task CheckExecution(PluginTemplate template, Func<CancellationToken, Task<HttpResponseMessage>> respond,
        bool expectedSuccess, string? expectedValue = null, string? expectedLabel = null, TimeSpan? timeout = null)
    {
        using var executor = Executor(respond, timeout);
        var instance = Instance();
        var success = await executor.ExecuteInstanceAsync(instance, template);
        Check(success == expectedSuccess, "执行结果不符合预期");
        var info = InfoService.Instance;
        var value = info.GetValue(instance.Id + ".next");
        if (!expectedSuccess)
        {
            Check(value == PluginConstants.STATUS_ERROR, "原有失败行为应显示错误");
            return;
        }
        using var fallback = JsonDocument.Parse(NewYearNative.GetCountdownJson(DateTime.Today));
        Check(value == (expectedValue ?? fallback.RootElement.GetProperty("rest").GetRawText()), "倒计时天数不正确");
        Check(info.GetValue("PROP.Label.DASH." + instance.Id + ".next") == (expectedLabel ?? "距离元旦"), "节假日名称不正确");
    }

    private static async Task RollbackTest()
    {
        using var executor = Executor(_ => Json("{\"holiday\":{\"name\":\"春节\",\"rest\":7}}"));
        var step = LoadTemplate().Execution.Steps![0];
        step.Process = new() { new PluginTransform { TargetVar = "额外变量", SourceVar = "name", Function = "regex_replace", Pattern = ".+", To = "覆盖" } };
        var context = new Dictionary<string, string> { ["name"] = "已有名称", ["rest"] = "20", ["额外变量"] = "保持" };
        var method = typeof(PluginExecutor).GetMethod("ExecuteStepAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(executor, new object[] { Instance(), step, context, "", CancellationToken.None })!;
        Check(context.Count == 3 && context["name"] == "已有名称" && context["rest"] == "20" && context["额外变量"] == "保持",
            "失败步骤必须丢弃所有临时修改并保留先前结果");
    }

    private static async Task CancellationTest()
    {
        using var cancel = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = Executor(_ => { started.SetResult(); return finish.Task; });
        var instance = Instance();
        InfoService.Instance.InjectValue(instance.Id + ".next", "保持原值");
        var execution = executor.ExecuteInstanceAsync(instance, LoadTemplate(), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        try
        {
            Check(!await execution.WaitAsync(TimeSpan.FromSeconds(2)), "取消应返回失败");
            Check(InfoService.Instance.GetValue(instance.Id + ".next") == "保持原值", "取消不应写入元旦倒计时或错误状态");
        }
        finally { finish.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }

    private static async Task CancellationFailureTest()
    {
        using var cancel = new CancellationTokenSource();
        using var executor = Executor(_ =>
        {
            cancel.Cancel();
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("取消同时网络失败"));
        });
        var instance = Instance();
        InfoService.Instance.InjectValue(instance.Id + ".next", "保持原值");
        Check(!await executor.ExecuteInstanceAsync(instance, LoadTemplate(), cancel.Token), "取消应返回失败");
        Check(InfoService.Instance.GetValue(instance.Id + ".next") == "保持原值", "网络失败覆盖了已取消插件的显示");
    }

    private static Task RecoveryTest() => RecoveryTest("{\"holiday\":null}");

    private static async Task RecoveryTest(string firstResponse)
    {
        int calls = 0;
        using var executor = Executor(_ => Json(++calls == 1 ? firstResponse :
            "{\"holiday\":{\"name\":\"春节\",\"date\":\"2027-02-06\",\"rest\":12}}"));
        var template = LoadTemplate();
        template.Execution.Steps![0].CacheMinutes = 60;
        var instance = Instance();
        Check(await executor.ExecuteInstanceAsync(instance, template), "第一次应回退成功");
        Check(InfoService.Instance.GetValue("PROP.Label.DASH." + instance.Id + ".next") == "距离元旦", "第一次应显示元旦");
        Check(await executor.ExecuteInstanceAsync(instance, template), "主源恢复后应执行成功");
        Check(calls == 2 && InfoService.Instance.GetValue(instance.Id + ".next") == "12", "失败结果不能缓存");
        Check(InfoService.Instance.GetValue("PROP.Label.DASH." + instance.Id + ".next") == "距离春节", "主源恢复后应更新名称");
    }

    private static void CheckCountdown(DateTime today, string expectedDate, int expectedRest)
    {
        using var json = JsonDocument.Parse(NewYearNative.GetCountdownJson(today));
        Check(json.RootElement.GetProperty("name").GetString() == "元旦", "应标明元旦");
        Check(json.RootElement.GetProperty("date").GetString() == expectedDate, "目标日期不正确");
        Check(json.RootElement.GetProperty("rest").GetInt32() == expectedRest, "自然日差不正确");
    }

    private static async Task Run(string name, Func<Task> test)
    {
        try { await test(); _passed++; Console.WriteLine("通过：" + name); }
        catch (Exception ex) { _failed++; Console.WriteLine($"失败：{name}：{ex.GetBaseException().Message}"); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(cancellationToken);
    }
}
