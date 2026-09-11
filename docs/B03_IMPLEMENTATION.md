# B03 候选包实施约定

基于B02提交6dd4e76，PR8运行34548668020三平台及全部前置检查成功。工作分支agent/b03-candidate-packages。Codex规划与独立验收；Grok、agy按文件独占编码。不得修改主仓库或安装设备。

## S1：可复核的开发候选目录

先实现明确不可发布的Staging目录和报告，不读取现有签名私钥，不生成可冒充签名更新的ZIP。输入为Server、ControlCenter、Apk三个已构建文件，调用B02真实版本校验；输出只限独立工作区outputs内的新GUID目录，不能覆盖或删除旧文件。支持带空格/中文路径，拒绝输出链接/越界路径。

Grok独占scripts/release/New-CandidateRun.ps1及需要的scripts/release内部helper，负责输出与报告。参数Server、ControlCenter、Apk、OutputRoot（默认outputs/candidates）、可选Aapt2Path。当前Mode仅Staging；不能自动进入签名发布。创建新目录后记录running/failed/complete，任何失败非零；成功文件为payload/PhoneDeck.Server.exe、payload/PhoneDeck.ControlCenter.exe、payload/PhoneDeck.apk及candidate.json。报告schemaVersion=1、mode=staging、releasable=false、status、runId、createdUtc、sourceCommit、sourceDirty、versions（描述快照）、files[{name,path,size,sha256}]、validation对象。sourceCommit只是当前工作树来源，不能冒充二进制独立构建证明；报告须同时记录PE ProductVersion，保留其提交后缀供review。不要把用户文件路径、环境秘密、data写入报告。报告原子写入，失败目录保留供检查。

拷贝前后核对SHA256，校验实际暂存副本并确保与报告一致；上游文件变动必须失败。暂不实现签名密钥参数、自动安装或修改运行时更新协议。

agy本轮先只读规划scripts/release/tests/Test-CandidateRun.ps1的实际控制流测试，待接口实现后单独授权编码；不得改Grok文件。覆盖失败输出/越界/重复运行不覆盖/哈希/真实版本错误；测试保留独立夹具，不递归清理。

## 后续切片

S2：渠道与发布历史策略、APK安装证书实际核验、发布序号唯一性、缺材料失败；仅临时生成的测试密钥用于夹具测试。S3：首次安装/旧版接入包及受保护签名流程；复用现有本地Install-UpdateSupport脚本，不扩展远程执行能力。S4：候选来源、证书/发布者指纹、安装与回退说明，进入逐设备验收。任何未签名开发产物都不称正式可安装更新包，Mac/iOS继续明确未实测边界。
