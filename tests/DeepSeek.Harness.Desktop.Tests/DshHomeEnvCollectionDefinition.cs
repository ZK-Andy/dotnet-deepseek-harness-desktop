using Xunit;

namespace DeepSeek.Harness.Desktop.Tests;

/// <summary>「dsh-home-env」集合必须真串行：集合内测试都改写进程级
/// DSH_DESKTOP_DSH_HOME 环境变量，并行执行会互相污染（HarnessRuntimeHostTests 与
/// SharedHomeContractTests 历史注释声称串行但此前无定义=实际并行，CI 偶发
/// Directory-not-empty 清理竞态实证）。</summary>
[CollectionDefinition("dsh-home-env", DisableParallelization = true)]
public class DshHomeEnvCollectionDefinition;