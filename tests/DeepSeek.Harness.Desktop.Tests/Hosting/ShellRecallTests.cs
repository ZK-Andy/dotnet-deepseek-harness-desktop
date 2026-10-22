namespace DeepSeek.Harness.Desktop.Tests.Hosting;

/// <summary>唤回单点：二启通知先于建窗，请求必须挂起到窗口就绪后补发一次；就绪后即投即达。</summary>
public class ShellRecallTests
{
    /// <summary>验证建窗前到达的请求被挂起，Attach 时补发恰一次；重复 Attach 不重复补发。</summary>
    [Fact]
    public void RequestBeforeAttach_FiresOnceOnAttach()
    {
        var recall = new ShellRecall();
        int calls = 0;

        recall.Request();
        recall.Request(); // 多次请求只补发一次：唤回幂等，堆积没有意义
        Assert.Equal(0, calls);

        recall.Attach(() => calls++);
        Assert.Equal(1, calls);

        recall.Attach(() => calls++); // 重新挂载不重放已消费的挂起请求
        Assert.Equal(1, calls);
    }

    /// <summary>验证窗口就绪后的请求即投即达，不经过挂起通道。</summary>
    [Fact]
    public void RequestAfterAttach_FiresImmediately()
    {
        var recall = new ShellRecall();
        int calls = 0;
        recall.Attach(() => calls++);

        recall.Request();
        recall.Request();

        Assert.Equal(2, calls);
    }

    /// <summary>验证 Detach 后请求不再触达已关闭窗口的旧 target（关窗后迟到二启通知的收口点）。</summary>
    [Fact]
    public void Detach_StopsDeliveryToOldTarget()
    {
        var recall = new ShellRecall();
        int calls = 0;
        recall.Attach(() => calls++);
        recall.Request();
        Assert.Equal(1, calls);

        recall.Detach();
        recall.Request(); // 解挂后只记挂起，不得再调旧 target
        Assert.Equal(1, calls);
    }

    /// <summary>验证 Detach 清掉解挂前挂起的请求：重新挂载不重放陈旧请求（否则窗口刚关就自动弹出）。</summary>
    [Fact]
    public void DetachClearsPending_NoStaleReplayOnReattach()
    {
        var recall = new ShellRecall();
        int calls = 0;
        recall.Request(); // 挂起
        recall.Detach();  // 窗口关闭：清掉挂起
        recall.Attach(() => calls++);
        Assert.Equal(0, calls);
    }

    /// <summary>验证挂起请求消费后，后续请求仍按即时通道投递（挂起标记被清零而非长期为真）。</summary>
    [Fact]
    public void PendingConsumedOnce_SubsequentRequestsUseAttachedTarget()
    {
        var recall = new ShellRecall();
        int calls = 0;
        recall.Request();
        recall.Attach(() => calls++);
        Assert.Equal(1, calls);

        recall.Request();
        Assert.Equal(2, calls);
    }

    /// <summary>验证 Request（监听线程）与 Attach（UI 线程）并发时恰好投递一次：挂起标记与目标赋值
    /// 之间没有丢失窗口，也没有同一请求被重放两次。</summary>
    [Fact]
    public async Task ConcurrentRequestAndAttach_DeliversExactlyOnce()
    {
        for (int round = 0; round < 50; round++)
        {
            var recall = new ShellRecall();
            int calls = 0;
            using var barrier = new Barrier(2);
            var attach = Task.Run(() =>
            {
                barrier.SignalAndWait();
                recall.Attach(() => Interlocked.Increment(ref calls));
            });
            var request = Task.Run(() =>
            {
                barrier.SignalAndWait();
                recall.Request();
            });

            await Task.WhenAll(attach, request);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
    }
}
