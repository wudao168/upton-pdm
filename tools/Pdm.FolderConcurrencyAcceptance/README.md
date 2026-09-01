# 项目目录并发回归验证

用于复现整套存档登记图档时的目录死锁。仅连接本机 MySQL，连接串不得指定数据库；工具会创建名称为 `pdm_folder_<随机值>_qa` 的独立数据库，不读写生产项目或 CAD 文件。运行后保留测试库便于检查，并输出库名。

设置 `PDM_FOLDER_QA_CONNECTION` 为有权创建测试库的本机 MySQL 连接串，然后运行：

```powershell
.local/dotnet-sdk/dotnet.exe run --project tools/Pdm.FolderConcurrencyAcceptance -c Release
```

验证内容：无变更目录不重写、历史图档回填不跨项目、新子项目及重命名、同项目与不同项目共160次并发初始化，以及1213/1205错误的完整事务重试、重试上限、非暂态错误和取消行为。故障注入只安装在新建的QA库内。全部通过返回0，有任何失败返回1。
