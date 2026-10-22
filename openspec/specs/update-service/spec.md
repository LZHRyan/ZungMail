# update-service Specification

## Purpose
为 ZungMail 提供可自托管的升级服务：开发者在后台管理页上传完整安装包并配置版本信息，客户端通过固定接口检测与下载，服务可 Docker 部署且数据可持久化。

## Requirements

### Requirement: 管理员登录与会话

升级服务 SHALL 仅允许持有管理员口令的访问者进入管理页与调用管理接口，会话凭据使用 HttpOnly Cookie；口令取自环境变量，未提供时在首次启动生成并以一次性初始口令文件公布。

#### Scenario: 口令正确

- **WHEN** 管理员在登录页提交与环境变量一致的口令
- **THEN** 服务建立 HttpOnly 会话并返回管理数据，管理页进入版本列表

#### Scenario: 未认证访问管理接口

- **WHEN** 未携带有效会话调用 `/api/admin/releases`
- **THEN** 服务返回 401，管理页跳转登录

#### Scenario: 连续错误口令

- **WHEN** 同一来源连续多次提交错误口令
- **THEN** 服务在冷却期内直接返回 429，冷却结束后才允许再次尝试

#### Scenario: 修改口令

- **WHEN** 已登录管理员提交旧口令与新口令
- **THEN** 服务保存新口令的 PBKDF2 哈希，旧口令与会话立即失效

### Requirement: 安装包上传与版本配置

管理页 SHALL 支持上传完整安装包（.exe / .msi / .zip）并配置版本号、渠道、更新说明、是否强制更新与最低可升级版本；上传成功后为草稿状态，发布前对客户端不可见。

#### Scenario: 上传并保存草稿

- **WHEN** 管理员上传 1.1.0 安装包并填写更新说明
- **THEN** 服务保存文件并记录版本、渠道、大小、SHA256、上传时间，列表显示为草稿

#### Scenario: 版本号不合法

- **WHEN** 上传时版本号填写为 `abc` 或 `1.1`
- **THEN** 服务拒绝请求并返回版本号格式要求，不产生记录

#### Scenario: 文件类型不受支持

- **WHEN** 上传的文件扩展名不在允许列表内
- **THEN** 服务返回 400，临时文件被清理

#### Scenario: 版本号重复

- **WHEN** 同一渠道下已存在相同版本号的版本
- **THEN** 服务拒绝上传或修改并提示冲突

### Requirement: 版本发布、下线与删除

管理页 SHALL 支持发布、取消发布与删除版本，并展示下载次数；客户端检测接口只返回已发布版本。

#### Scenario: 发布新版本

- **WHEN** 管理员把 1.1.0 草稿发布
- **THEN** 检测接口对当前版本 1.0.0 的客户端返回 1.1.0

#### Scenario: 取消发布

- **WHEN** 管理员把 1.1.0 改为草稿
- **THEN** 检测接口不再返回该版本，已下载的客户端不受影响

#### Scenario: 删除版本

- **WHEN** 管理员删除 1.1.0
- **THEN** 版本记录与安装包文件被删除，原下载地址返回 404

#### Scenario: 下载次数统计

- **WHEN** 客户端完成一次安装包下载
- **THEN** 该版本下载次数加一并出现在管理页统计中

### Requirement: 客户端检测接口

升级服务 SHALL 提供 `GET /api/update/check`，接受 `current`、`channel`、`os`、`arch` 参数，返回已发布版本中高于当前版本的最高版本；没有可用更新时返回 `update: null`。

#### Scenario: 存在更高版本

- **WHEN** 请求 `current=1.0.0&channel=stable`
- **THEN** 返回 1.1.0 的版本号、更新说明、是否强制、大小、SHA256 与下载地址

#### Scenario: 已是最新

- **WHEN** 请求 `current=1.2.0` 且已发布版本最高为 1.1.0
- **THEN** 返回 `update: null` 与服务器时间

#### Scenario: 渠道不匹配

- **WHEN** 请求 `channel=beta` 且只有 stable 渠道版本
- **THEN** 返回 `update: null`

#### Scenario: 参数缺失或非法

- **WHEN** 请求缺少 `current` 或版本号无法解析
- **THEN** 服务返回 400 与错误说明，不返回版本内容

### Requirement: 安装包下载接口

升级服务 SHALL 提供 `GET /api/update/download/{id}` 输出安装包字节流，返回 Content-Length、文件名与 SHA256 响应头，并支持 Range 断点请求。

#### Scenario: 完整下载

- **WHEN** 客户端请求已发布版本的下载地址
- **THEN** 服务返回 200，字节数、文件名与 SHA256 与上传记录一致

#### Scenario: 断点续传

- **WHEN** 请求携带 `Range: bytes=0-1023`
- **THEN** 服务返回 206 与对应字节片段及 Content-Range

#### Scenario: 下载未发布或不存在版本

- **WHEN** 请求草稿版本或不存在 id 的下载地址
- **THEN** 服务返回 404

### Requirement: Docker 部署与数据持久化

升级服务 SHALL 提供 Dockerfile 与 compose 示例，安装包与版本记录保存在可挂载数据目录；容器重建并挂载同一卷后数据仍可用，且提供健康检查端点。

#### Scenario: 容器首次启动

- **WHEN** 使用 compose 启动并挂载数据卷、设置管理员口令环境变量
- **THEN** 管理页可访问，上传的安装包与版本记录写入数据卷

#### Scenario: 容器重建

- **WHEN** 删除并重建容器但复用同一数据卷
- **THEN** 版本列表、安装包与管理员口令哈希保持

#### Scenario: 健康检查

- **WHEN** 访问 `/healthz`
- **THEN** 服务返回 200，包含数据目录可写与版本数量等状态

### Requirement: 客户端地址探测与接口约定

升级服务 SHALL 提供 `GET /api/update/ping`，返回协议版本与服务标识，供客户端在只配置根地址时确认服务可用。

#### Scenario: 客户端只填根地址

- **WHEN** 客户端请求 `GET /api/update/ping`
- **THEN** 服务返回协议版本 `1` 与服务名 `ZungMail.UpdateServer`

#### Scenario: 部署在反向代理子路径

- **WHEN** 服务被代理到 `/updates` 前缀
- **THEN** 客户端用「根地址 + 内置路径」仍能检测，管理页静态资源按相对路径加载
