using PolyMigrate.Cli;

// 進入點只做一件事:把 Ctrl-C 接成合作式取消(讓長時間的 probe/fetch 能乾淨中止),
// 其餘全交給可測的 Cli.RunAsync。exit code 契約見 Cli。
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // 第一次 Ctrl-C:合作式取消(async 指令會乾淨收尾)。
    // 第二次:不再攔截(e.Cancel 維持 false)→ 讓 runtime 直接終止——
    // 同步指令(extract/thumbs/verify)不看 token,否則第一次就會把 Ctrl-C 永久吞掉、無法中斷。
    if (cts.IsCancellationRequested)
    {
        return;
    }
    e.Cancel = true;
    try
    {
        cts.Cancel();
    }
    catch (ObjectDisposedException)
    {
        // 工作已結束、cts 已釋放,行程正要退出——忽略
    }
};

return await Cli.RunAsync(args, cts.Token);
