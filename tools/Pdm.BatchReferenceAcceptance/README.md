# 整体存档引用版本回归

使用实际插件程序集和内存 HTTP 替身，不连接 SolidWorks COM、不访问生产 API。
仅在指定 QA 目录创建少量测试文件。

```powershell
& .local/dotnet-sdk/dotnet.exe build tools/Pdm.BatchReferenceAcceptance -c Release -p:NuGetAudit=false
& tools/Pdm.BatchReferenceAcceptance/bin/Release/net48/Pdm.BatchReferenceAcceptance.exe src/Pdm.SolidWorks.Addin/bin/Release/net48/Upton.Pdm.SolidWorks.Addin.dll .local/qa-batch-reference
```

覆盖旧提交快照与刷新后的设计树分离、重复实例去重、按源文件哈希匹配历史版本、
未匹配内容阻止提交、取消、同批次子件身份及版本传播，以及提交前引用校验。
这不替代真实主装配体整体存档验收。
