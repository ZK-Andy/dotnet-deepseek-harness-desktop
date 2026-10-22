namespace DeepSeek.Harness.Desktop.Hosting;

/// <summary>唤回请求的单点：托盘点击、二启通知、页面命令都投到这里。窗口创建前的请求先挂起，
/// 窗口就绪（<see cref="Attach"/>）时补发一次——二启仲裁发生在建窗之前，丢了这次就停在
/// 「有主实例但没有窗」。</summary>
internal sealed class ShellRecall
{
    private readonly object _sync = new();
    private Action? _target;
    private bool _pending;

    /// <summary>挂上窗口侧唤回动作（UI 线程调用）；有挂起请求则立即补发一次。</summary>
    /// <param name="target">唤回动作（显示并置前主窗）。</param>
    public void Attach(Action target)
    {
        bool fire;
        lock (_sync)
        {
            _target = target;
            fire = _pending;
            _pending = false;
        }

        if (fire)
        {
            target();
        }
    }

    /// <summary>解挂窗口动作（窗口关闭时调用）：此后请求只记挂起，不再把手伸向已销毁的窗口。</summary>
    public void Detach()
    {
        lock (_sync)
        {
            _target = null;
            _pending = false;
        }
    }

    /// <summary>请求唤回主窗（任意线程）；窗口未就绪时记挂起。</summary>
    public void Request()
    {
        Action? target;
        lock (_sync)
        {
            target = _target;
            if (target is null)
            {
                _pending = true;
                return;
            }
        }

        target();
    }
}
