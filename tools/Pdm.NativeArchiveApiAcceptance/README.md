# 真实 CAD 准备结果到存档 API 的隔离验收

此工具将 SolidWorks 原生准备阶段的真实文件副本和规范引用快照送入现有 PDM API。它复用 `PdmApiFactory`，强制使用内存数据库及新建的独立文件库，不读取生产账号、不连接生产数据库。

运行：

```powershell
& .\.dotnet\dotnet.exe tools/Pdm.NativeArchiveApiAcceptance/bin/Release/net10.0/Pdm.NativeArchiveApiAcceptance.dll `
  'H:\codex file\pdm' '<已存在的 tmp 原生验收目录>' '<尚不存在的 tmp API 验收目录>'
```

工具绑定 `127.0.0.1` 随机端口，将 `{ url, token, nativeRoot }` 写入 API 验收目录的 `ready.json`。调用方只允许提交一次：向 `url` 发送 JSON，添加 `X-QA-Token: token`。所有 `path` 和 `sourcePath` 必须在命令行指定的原生验收目录内，不能使用目录链接。

```json
{
  "files": [
    {
      "key": "用于本次隔离清单的 GUID",
      "path": "QA 目录内稳定的上传副本绝对路径",
      "sourcePath": "QA 目录内源文件绝对路径",
      "sourceSha256": "源文件 SHA256 大写十六进制",
      "isProjectRoot": true,
      "root": "CaptureArchiveSnapshot(...).CreateRoot() 返回的 JSON 对象",
      "properties": { "全局/示例属性": "值" }
    }
  ]
}
```

`root.documentId` 必须等于 `key`。所有非虚拟引用的 `documentId` 都必须存在于 `files[].key` 中。工具只映射隔离服务器分配的身份，保留原生实例路径、配置、数量和状态，不通过文件名补齐引用。必须包括唯一根装配及至少一张工程图。

验收依次检查：身份预留不产生正式图档；每文件预检；真实分块上传；单张图纸哈希错误使整批零版本；正常提交后忽略第一次回执内容，再重复请求及查询回执；无修改新批次不重复升版；库内文件字节与上传副本一致；源文件及上传副本始终未被改写。

调用完成后，HTTP 响应与 `result.json` 保存结果。工具自动退出并删除自己新建的临时上传及文件库，保留验收报告，也保留调用方的原生 CAD 文件。它验证真实 CAD 输出与实际 API 的衔接，不替代 MySQL 事务测试，也不模拟所有网络故障。
