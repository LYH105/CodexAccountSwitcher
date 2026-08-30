# Codex 账号管理

一个原生 Windows WPF 小工具，用于管理 `C:\Users\<用户名>\.codex\auth.json*` 账号槽位。

界面采用 iOS 18 风格的系统蓝、浅色玻璃分组与大圆角；不使用持续动画、实时模糊或后台轮询，保持轻量。

## 功能

- 识别 `auth.json`、`auth.json0`、`auth.json1` 等严格数字槽位；Team/Business/Enterprise 账号优先展示与 `account_id` 精确匹配的 Workspace 名称，个人套餐展示姓名和邮箱。内部槽位文件名不会出现在账号卡片中。
- Team 名称优先通过 Codex 的 `wham/accounts/check` 元数据校正，也会读取 JWT `organizations` 中与账号 ID 匹配的本地名称；单账号过期或查询失败不影响其他账号。
- 打开工具或手动点击刷新时，并发查询各账号的 ChatGPT 当前额度、重置时间与 credits；不再显示长期为空的“次要额度”。
- 每个账号可打开“精确分析”，按日期读取 Workspace 日级 Token 与 Breakdown，优先使用模型级未缓存输入、缓存输入、输出和 speed 计算 Codex 等价 Credits 与公开 API 等价成本。
- 精确分析原生移植自 `Codex Token API Cost Analyzer v4.4.0`：支持近 7 天、近 30 天和自定义日期，并展示逐模型明细、Fast、发生日价格、同速度价格及精确 Token 覆盖率。
- 点击“登录新账号”会启动官方 `codex login` 浏览器流程；登录凭据先写入隔离的临时 `CODEX_HOME`，验证完整后再导入。
- 一键交换目标槽位与 `auth.json`。旧的当前凭据会回存到目标槽位，不会被覆盖丢失；交换成功后完整关闭并重新启动 Store 版 ChatGPT。
- 非当前账号可确认后一键删除。删除不依赖 JWT 是否有效或 JSON 能否解析，因此失效、过期或损坏的保存账号也能直接移除；当前账号必须先切换后才能删除。
- 使用同目录临时文件、排他锁、SHA-256 和事务日志；崩溃后下次启动会自动恢复。
- 不刷新 OAuth，不把 access token / refresh token 暴露给界面或写入日志。
- 登录取消或失败时保持原账号不变；若登录的是已有槽位账号，会用新凭据作为当前账号并把旧当前账号回存到该槽位。
- 无后台轮询、无托盘常驻；关闭窗口后进程退出。

精确分析中的美元金额表示相同工作量使用公开 OpenAI API 时的等价估算，不是 ChatGPT/Codex 订阅账单。Analytics 接口缺少模型级 Token 时会明确标记“回退估算”，不会冒充精确结果。

## 使用

双击发布目录中的 `CodexAccountSwitcher.exe`。切换后工具只结束 Store 包内的 `ChatGPT.exe` 和配套 `resources\\codex.exe`，等待退出后通过 AppUserModelID 重启 ChatGPT；不会关闭 `Code.exe` 或 VS Code 扩展目录里的 `codex.exe`。

发布版依赖本机 `.NET 8 Desktop Runtime`；当前机器已经安装。工具关闭后不会留下后台进程。

认证文件含有密码级敏感令牌，请勿上传、提交或分享。

## 开发验证

```powershell
dotnet run --project tests\CodexAccountSwitcher.Tests\CodexAccountSwitcher.Tests.csproj -c Release
dotnet build src\CodexAccountSwitcher\CodexAccountSwitcher.csproj -c Release
```

测试全部使用虚构 JWT 和临时目录，不会交换真实的 `.codex\auth.json`。
