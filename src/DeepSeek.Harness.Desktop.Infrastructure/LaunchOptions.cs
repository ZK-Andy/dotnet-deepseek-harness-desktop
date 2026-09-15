namespace DeepSeek.Harness.Desktop.Infrastructure;

/// <summary>
/// 启动期 A 类配置（ADR composition-root-value-flow-pipeline 批次 3）：dev 判定与自动隔离结论由组合根
/// 裸 bool 升为类型化配置值，<see cref="Resolve"/> 单点解析一次，消费方经构造注入所需不可变值。
/// 这是「配置解析一次 + 依赖注入」，与被否决的「阶段间传 <c>BootstrapContext</c>」不同构——后者把
/// 可变阶段产出打包在阶段间流动，本类型是环境派生的不可变配置（边界见主 ADR Alternatives）。
/// </summary>
public sealed record LaunchOptions
{
    /// <summary>是否为 dev 运行时（只认显式环境标记，判定见 <see cref="DevEnvironment.IsDevRuntime"/>）。</summary>
    public bool IsDev { get; init; }

    /// <summary>本次启动是否把 DSH_HOME 自动隔离到仓库内 dev-home（仅 dev 且未显式覆盖 home 时为真）。</summary>
    public bool DevAutoIsolated { get; init; }

    /// <summary>按 dev 形态推导 ApplicationId（后缀规则单一来源 <see cref="DevEnvironment.ApplicationIdFor"/>）。</summary>
    /// <param name="baseId">正式版 ApplicationId。</param>
    /// <returns>dev 形态带 <see cref="DevEnvironment.AppIdSuffix"/> 后缀，否则原值。</returns>
    public string ApplicationIdFor(string baseId) => DevEnvironment.ApplicationIdFor(baseId, IsDev);

    /// <summary>
    /// 单点解析启动配置：读 dev 环境标记，并在 dev 且未显式覆盖 DSH_HOME 时把 home 指向仓库内
    /// <c>.cache/dev-home</c>（环境读写归 Infrastructure 边界；原组合根 <c>ResolveRuntimeAndDev</c> 的等价逻辑）。
    /// </summary>
    /// <param name="log">日志回调（隔离生效与误判诊断各一条）。</param>
    /// <returns>解析一次的不可变配置值。</returns>
    public static LaunchOptions Resolve(Action<string> log)
    {
        string? devRuntimeDir = Environment.GetEnvironmentVariable(DevEnvironment.RuntimeDirEnv);
        string? devFlag = Environment.GetEnvironmentVariable(DevEnvironment.DevFlagEnv);
        bool isDev = DevEnvironment.IsDevRuntime(devRuntimeDir, devFlag);
        bool devAutoIsolated = false;
        if (isDev && Environment.GetEnvironmentVariable(DevEnvironment.HomeOverrideEnv) is null)
        {
            string? devHome = DevEnvironment.DeriveDefaultDevHome(devRuntimeDir, AppContext.BaseDirectory);
            if (devHome is not null)
            {
                Environment.SetEnvironmentVariable(DevEnvironment.HomeOverrideEnv, devHome);
                devAutoIsolated = true;
                log($"[host] 开发运行时：DSH_HOME 隔离到 {devHome}；ApplicationId 带 .dev 后缀，可与正式版并存");
            }
        }
        else if (!isDev &&
                 DevEnvironment.DeriveDefaultDevHome(null, AppContext.BaseDirectory) is not null)
        {
            // dev 判定改显式标记后的唯一残留风险（R2 评审）：贡献者在仓库内跑却忘带
            // DSH_DESKTOP_DEV=1 —— 判定按设计走打包产品语义，但值得一条 host.log 诊断指路
            log("[host] 疑似仓库内开发运行但未设 DSH_DESKTOP_DEV=1：按打包产品处理（共享真实 home，无 dev 隔离）");
        }

        return new LaunchOptions { IsDev = isDev, DevAutoIsolated = devAutoIsolated };
    }
}
