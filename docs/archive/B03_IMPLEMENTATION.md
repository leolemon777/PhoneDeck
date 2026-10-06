# B03 候选包实施约定

## S4a 事务基础（0445662全云端通过后的下一切片）

分支agent/b03-release-transaction。Grok独占新增scripts/release/ReleaseTransaction.ps1和tests/Test-ReleaseTransaction.ps1。仅可点载入的内部库，无签名CLI、无私钥参数、无releasable=true。本轮调用与写入仅限独立outputs测试夹具，不读取或更改现有发行历史。S2只读门槛与运行时公钥保持不变。

历史兼容schemaVersion=1/releases数组；严格正整型、全局唯一递增。提交条目含sequence、transactionId、committedUtc。锁为HistoryPath.lock，FileShare.None并持有至提交/中止；journal记录transactionId、sequence、状态pending/committed/aborted及历史基线哈希，原子写入。缺历史拒绝，显式空数组可初始化；锁文件存在不等于锁被占用，进程死亡不等于签名已验证。

Begin在锁内校验并建立pending；Commit为内部状态原语，原子追加历史后更新journal；Abort不得删除已提交序号。Recover必须重新获得同一锁：pending且历史无对应transactionId/sequence则保守标记aborted，历史保持原样；历史存在精确对应则修复journal为committed，冲突/篡改拒绝，不能仅凭sequence匹配承认他人事务。提交时核对历史基线，拒绝非协作写入。所有结果mode=transaction-foundation/releasable=false；不删除产物、目录或超时强解锁。句柄释放在finally，路径链接与越界拒绝。

测试保留纯数据夹具，覆盖正常提交/显式空历史/缺失或畸形历史/序号重复与倒退/真实子进程锁竞争/同序号竞争/中止/中断两侧恢复/不降低已提交历史/冲突journal/历史外改。测试只终止自己创建的夹具子进程，不对真实代理或产品进程操作。S4b再接入既有五文件ZIP、实际签名核验和可信构建来源；只有包装器核验通过才可调用内部Commit，此基础库不授予发行资格。

基于B02提交6dd4e76，PR8运行34548668020三平台及全部前置检查成功。工作分支agent/b03-candidate-packages。Codex规划与独立验收；Grok、agy按文件独占编码。不得修改主仓库或安装设备。

## S1：可复核的开发候选目录

先实现明确不可发布的Staging目录和报告，不读取现有签名私钥，不生成可冒充签名更新的ZIP。输入为Server、ControlCenter、Apk三个已构建文件，调用B02真实版本校验；输出只限独立工作区outputs内的新GUID目录，不能覆盖或删除旧文件。支持带空格/中文路径，拒绝输出链接/越界路径。

Grok独占scripts/release/New-CandidateRun.ps1及需要的scripts/release内部helper，负责输出与报告。参数Server、ControlCenter、Apk、OutputRoot（默认outputs/candidates）、可选Aapt2Path。当前Mode仅Staging；不能自动进入签名发布。创建新目录后记录running/failed/complete，任何失败非零；成功文件为payload/PhoneDeck.Server.exe、payload/PhoneDeck.ControlCenter.exe、payload/PhoneDeck.apk及candidate.json。报告schemaVersion=1、mode=staging、releasable=false、status、runId、createdUtc、sourceCommit、sourceDirty、versions（描述快照）、files[{name,path,size,sha256}]、validation对象。sourceCommit只是当前工作树来源，不能冒充二进制独立构建证明；报告须同时记录PE ProductVersion，保留其提交后缀供review。不要把用户文件路径、环境秘密、data写入报告。报告原子写入，失败目录保留供检查。

拷贝前后核对SHA256，校验实际暂存副本并确保与报告一致；上游文件变动必须失败。暂不实现签名密钥参数、自动安装或修改运行时更新协议。

agy本轮先只读规划scripts/release/tests/Test-CandidateRun.ps1的实际控制流测试，待接口实现后单独授权编码；不得改Grok文件。覆盖失败输出/越界/重复运行不覆盖/哈希/真实版本错误；测试保留独立夹具，不递归清理。

## 后续切片

S2：渠道与发布历史策略、APK安装证书实际核验、发布序号唯一性、缺材料失败；仅临时生成的测试密钥用于夹具测试。S3：首次安装/旧版接入包及受保护签名流程；复用现有本地Install-UpdateSupport脚本，不扩展远程执行能力。S4：候选来源、证书/发布者指纹、安装与回退说明，进入逐设备验收。任何未签名开发产物都不称正式可安装更新包，Mac/iOS继续明确未实测边界。

## S2 首批接口（基于S1提交d1c089c / PR9）

分支agent/b03-release-policy。Grok独占ReleasePolicy.ps1、Get-ApkInstallCertSha256.ps1、Test-ReleaseEligibility.ps1、release-policy.example.json及tests/Test-ReleasePolicy.ps1，均在scripts/release内。S1 core/helper/test不改。本批入口只读检查，不签名、不更改候选或历史，不把通过检查称为已发布。历史原子锁定/占用与签名提交留给真正发布事务，不能在签名前写入已发布记录。

示例策略schemaVersion=1，channels数组包含id、enabled=false、apkInstallCertSha256空、publisherPublicKeySha256空；不填虚构正式指纹。入口参数CandidateDirectory、PolicyPath、HistoryPath、Channel、PublisherPublicKeyPath、ApkSignerPath（仅实际公钥PEM输入，无私钥参数）。输出JSON eligible（预检查结果）、releasable=false、reasons及核验过的指纹/序号；拒绝非零，允许仅表示材料预检查通过，尚未签名。

检查实际候选三文件哈希/大小、无输出目录路径逃逸、complete/staging且描述严格整型；策略enabled且两个完整64hex指纹；apksigner verify --print-certs成功且单签名者证书SHA256等于策略；RSA公钥导出SPKI规范字节再SHA256等于策略。历史schemaVersion=1、releases数组，其sequence严格正整数且全局唯一，候选sequence必须大于历史最大值；缺历史文件拒绝，显式空数组才表示新渠道初始化。sourceDirty=true拒绝；sourceCommit只表示工作树来源，不假装验证二进制来源。所有拒绝保持候选/历史字节不变。实际签名发布仍需额外检查构建来源与事务锁，不在S2预检查中提前声称完成。

测试使用纯数据和独立临时公钥，不访问现有签名材料。可通过显式参数对B02实际APK运行证书读取，但读到的开发证书不自动成为批准渠道。全套正式材料尚未配置时，以可复核拒绝结果为准。

## S3 开发包组装（与S2文件独立）

agy独占scripts/release/New-LocalCandidatePackages.ps1及tests/Test-LocalCandidatePackages.ps1。参数CandidateDirectory、OutputRoot（默认outputs/local-packages）、Aapt2Path。只接收完整S1候选并重新核对固定三文件/路径/哈希/大小/实际版本；不信任candidate.json单方面的complete状态。输出新GUID目录含PhoneDeck-Portable-STAGING.zip、PhoneDeck-Enrollment-STAGING.zip和packages.json，均为staging/releasable=false，原子报告、独立运行、越界/链接拒绝、不覆盖/删除历史。

Portable仅固定两个EXE、PhoneDeck.apk、空data/、说明；可附本地手动启动cmd，仅为当前进程设置PHONEDECK_DATA_DIR=%~dp0data并启动本目录控制台，不执行它。入口明确控制台启动，不能直接运行Server（Server默认使用LocalAppData）。Enrollment原样附源码仓库Install-UpdateSupport.ps1/.cmd及固定EXE、APK与说明，不执行安装脚本。ZIP清单逐项固定且核对内容SHA256，绝不打包用户data或扫描安装目录。

说明必须写清APK渠道未经发行确认（不能断言它无签名或必定不同渠道）、虚拟音频/输入法及ADB等先决条件未随包安装，开发包不等于公开发行成品。已有PHONEDECK_DATA_DIR影响配置位置，因此不能只靠空data目录宣称隔离。报告记录ZIP大小/哈希及输入候选标识，不混入S2策略文件或假发布批准。

测试显式传真实S1 CandidateDirectory/Aapt2Path，实际解ZIP比对固定条目与输入哈希，重复隔离、篡改/越界/缺文件失败。仅复制独立临时夹具做负例，保留夹具不递归清理，不启动任何EXE/安装脚本，不改S1/S2产品/CI源码。
