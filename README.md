# TrafficMonitor Agent Status

Windows x64 TrafficMonitor 插件，通过本机 [Agent Status Runtime](https://github.com/aoang-git/Agent_Status_Runtime) 显示 Agent 当前状态。版本 0.2.0 使用 Runtime schema 3，完整替换旧 Hook 和日志采集链路。

任务栏显示 `Codex:工作中`、`Claude:调用工具`、`DeepSeek:待确认`、`Pi:本轮完成` 等结果。待确认优先于工具、工作；同级显示最近活动会话。点击项目可查看各会话、配置 Runtime 目录、来源和状态文案。

## 安装

先安装并运行 Runtime 0.3.0 或兼容 schema 3 的版本。默认连接文件位于 `%APPDATA%\AgentStatusRuntime\private\connection.json`，不需要复制令牌或填写端口。

解压发布包，在普通用户 PowerShell 7 中执行：

```powershell
.\Install-PluginFiles.ps1 -TrafficMonitorDirectory '<TrafficMonitor目录>'
```

安装器校验包内 SHA-256，备份旧插件和配置，精确移除 Claude、Cursor、Gemini 中指向旧插件目录的 Hook，替换本插件目录并配置当前用户登录启动 Bridge。备份位于 `%LOCALAPPDATA%\TrafficMonitorAgentStatus\backups`。其他插件、Agent 配置和 Runtime 不受影响。若 TrafficMonitor 已运行，安装器关闭并重新启动它；首次安装请自行启动 TrafficMonitor 并在任务栏项目中启用“Agent 状态”。

旧配置不迁移，首次切换使用新版默认值；再次安装保留版本 2 的显示配置。自包含发布包无需另装 .NET。

## 状态与来源

- Runtime 内置观察 Codex、Claude、DeepSeek DSH、Pi；其他 Agent 通过 Runtime 标准上报接入，插件会自动识别来源。
- Cursor、Gemini 旧 Hook 已移除，需要独立 Runtime 上报端才能显示。本插件不安装 Agent Hook，也不读取会话文件。
- 正常结束显示“本轮完成”，明确整项任务完成显示“任务完成”，中断显示“已中断”，失败显示“异常”。
- 正常消息在没有待处理工具、确认或证据缺口时，静默 3 秒后结束。普通活动默认 5 分钟、待确认默认 30 分钟无进展后显示“状态未知”。判断依据不作为状态后缀显示。
- 完成提示默认 5 秒；异常、未知、中断默认 15 秒。活动优先于结束提示，空闲任务栏留空。
- Runtime 只广播订阅后新活动。启动、断线、服务换代、事件缺口均清空旧任务，重新连接不会恢复历史任务。

## 诊断

```powershell
& '<TrafficMonitor目录>\plugins\AgentStatus\AgentStatusBridge.exe' ping
& '<TrafficMonitor目录>\plugins\AgentStatus\AgentStatusBridge.exe' snapshot
& '<TrafficMonitor目录>\plugins\AgentStatus\AgentStatusBridge.exe' start
& '<TrafficMonitor目录>\plugins\AgentStatus\AgentStatusBridge.exe' stop
```

`start/stop` 只管理显示 Bridge。Runtime 由其自身安装器和计划任务管理。详情窗口可选择其他 Runtime 运行目录，保存后自动重新订阅。

诊断日志：`%LOCALAPPDATA%\TrafficMonitorAgentStatus\diagnostics.log`。日志不记录令牌、对话、工具参数或 HTTP 响应正文。仅读取 connection.json，不访问写令牌文件或调用 ingest。

## 开发

依赖 .NET 10 SDK、MSVC x64 和 Windows SDK。详见 [构建与验证](docs/构建与验证.md) 和 [架构](docs/项目架构与安装方案.md)。

```powershell
.\build.ps1 -Toolchain '<同时包含VC和Windows Kits的目录>' -SelfContained
.\tests\Integration.ps1
```

构建输出在 build/x64，自包含发布包和校验文件在 dist，不提交生成产物。当前使用 TrafficMonitor API 6 和固定示例文本预留宽度，仅支持 Windows x64；状态准确性受 Runtime 对相应 Agent 版本和事件格式的覆盖限制。

## 许可证

原创代码采用 [MIT](LICENSE)。原生插件接口及引用的 Runtime 客户端代码见 [第三方声明](THIRD_PARTY_NOTICES.md)。
