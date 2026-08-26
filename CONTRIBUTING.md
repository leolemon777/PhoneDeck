# 参与 PhoneDeck 开发

PhoneDeck 当前是私人开发项目。所有修改应从最新 `main` 创建短期分支，并通过 Pull Request 合并。

## 推荐流程

```powershell
git switch main
git pull --ff-only origin main
git switch -c agent/<short-task-name>
```

完成后：

```powershell
git status
git diff --check
git add <本次相关文件>
git commit -m "<清晰描述结果>"
git push -u origin agent/<short-task-name>
```

## Pull Request 必须说明

- 解决什么问题；
- 改了哪些行为；
- 执行了哪些构建或测试；
- 哪些项目仍需真实手机、Windows、Mac、Typeless 或 USB 硬件验证；
- 是否改变协议、配置格式、安全边界或产品路线。

## 不接受

- 签名密钥、令牌、个人录音或个人配置；
- 把 APK、EXE、Gradle/.NET 构建缓存直接提交到源码历史；
- 未经批准开放任意远程命令执行；
- 没有迁移策略的配置或协议破坏性变更；
- 把尚未实现的计划描述成已完成功能。
