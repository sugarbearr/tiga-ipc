# Release Guide

本文件记录当前仓库已经实测通过的 npm 发版流程，目标对象是 Node 包：

- 包目录：`nodejs/mmap-napi`
- npm 包名：`@tiga-ipc/mmap`
- 当前平台前提：Windows

当前仓库的 Node 包是原生 N-API 插件，发版时会把 `index.node` 一起打进 npm tarball。因此，发布前必须先重新生成并刷新本地原生产物。

## 发布前提

执行发版前，请先确认以下条件：

1. 你在 Windows 环境中操作
2. 已安装可用的 `node`、`npm`、`cargo`
3. 当前 npm 登录账号对 `tiga-ipc` organization 有发布权限
4. 若账号或组织启用了发布 2FA，准备好可用的 OTP，或者使用支持 `bypass 2fa` 的 granular access token

## 目录约定

从仓库根目录进入：

```powershell
cd .\nodejs\mmap-napi
```

后续命令默认都在这个目录下执行。

## 发版前检查

先确认 registry、登录账号、组织权限和包名状态：

```powershell
npm config get registry
npm whoami
npm org ls tiga-ipc
npm view @tiga-ipc/mmap version
```

预期说明：

- `npm config get registry` 应返回 `https://registry.npmjs.org/`
- `npm whoami` 应返回当前发布账号
- `npm org ls tiga-ipc` 中应能看到当前账号，并具备发布权限
- 若包尚未发布，`npm view @tiga-ipc/mmap version` 会返回 `E404`

## 版本号策略

首次发版可以直接沿用当前版本。

如果要升级版本，推荐在工作区中使用“不自动创建 git tag / commit”的方式：

```powershell
npm version patch --no-git-tag-version --force
```

也可以显式指定版本：

```powershell
npm version 0.1.1 --no-git-tag-version --force
```

说明：

- `npm version` 默认会尝试创建 git commit 和 tag
- 当前仓库可能处于持续整理状态，推荐只修改版本文件，不在这一步自动做 git 动作

## 构建原生包

优先使用当前仓库已经验证过的构建方式：

```powershell
npm run build
```

说明：

- 该命令会通过 `scripts/build-addon.js` 调用 `@napi-rs/cli build --release`
- 构建完成后会自动执行 `scripts/sync-package-types.js`，补齐 JS helper 的类型定义
- 如果当前环境里 `index.node` 被占用，仍然可以使用下方的兜底方式手工刷新原生产物

兜底方式：

```powershell
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

## 发布前自检

先做一次 dry-run：

```powershell
npm run publish:check
```

预期看到：

- 包名为 `@tiga-ipc/mmap`
- 版本号为当前 `package.json` 中的版本
- tarball 中包含：
  - `LICENSE`
  - `README.md`
  - `index.d.ts`
  - `index.js`
  - `index.node`
  - `package.json`

## 正式发布

### 无 2FA 情况

```powershell
npm publish --access public
```

### 开启 2FA 情况

```powershell
npm publish --access public --otp=123456
```

说明：

- scoped public package 首次发布时，显式带上 `--access public` 更稳妥
- 如果 npm 返回如下错误：

```text
Two-factor authentication or granular access token with bypass 2fa enabled is required to publish packages.
```

说明当前发布会话还需要满足以下其一：

1. 提供有效 OTP
2. 使用支持 `bypass 2fa` 的 granular access token

## 发布后校验

发布成功后，立即执行：

```powershell
npm view @tiga-ipc/mmap version
npm view @tiga-ipc/mmap dist-tags --json
npm view @tiga-ipc/mmap versions --json
npm view @tiga-ipc/mmap --json
```

至少应确认：

1. `version` 返回刚发布的版本
2. `dist-tags.latest` 指向刚发布的版本
3. `versions` 数组包含该版本
4. `description`、`license`、`homepage`、`repository` 等元信息正确

## 建议的完整流程

这是当前仓库推荐直接执行的一组命令：

```powershell
cd .\nodejs\mmap-napi

npm config get registry
npm whoami
npm org ls tiga-ipc
npm view @tiga-ipc/mmap version

cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force

npm run publish:check
npm publish --access public

npm view @tiga-ipc/mmap version
npm view @tiga-ipc/mmap dist-tags --json
npm view @tiga-ipc/mmap versions --json
```

如果发布需要 OTP，则将正式发布命令替换为：

```powershell
npm publish --access public --otp=123456
```

## 回滚与止损

### 1. 最推荐：fix-forward

如果刚发布的版本有问题，优先发一个新版本修复，而不是直接删除已发布版本。

示例：

```powershell
npm deprecate @tiga-ipc/mmap@0.1.0 "0.1.0 has a publish issue, use >=0.1.1"
npm version patch --no-git-tag-version --force
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
npm publish --access public
```

### 2. 废弃某个错误版本

```powershell
npm deprecate @tiga-ipc/mmap@0.1.0 "0.1.0 is broken, use >=0.1.1"
```

取消废弃提示：

```powershell
npm deprecate @tiga-ipc/mmap@0.1.0 ""
```

### 3. 最后手段：unpublish

```powershell
npm unpublish @tiga-ipc/mmap@0.1.0
```

或强制撤掉整个包：

```powershell
npm unpublish @tiga-ipc/mmap --force
```

注意：

- `unpublish` 受 npm 官方策略限制，不应作为常规回滚手段
- 某个 `package@version` 一旦发布，即使之后 `unpublish`，也不能复用同一个版本号
- 对外包更推荐使用 `deprecate + 新版本修复`

## 常见问题

### 1. `npm publish` 提示 2FA 相关错误

优先检查：

1. 是否提供了当前有效的 OTP
2. 当前登录方式是否满足组织的发布安全策略
3. 使用的 token 是否支持 `bypass 2fa`

### 2. `npm publish` 提示 scope 无权限

优先检查：

1. 当前账号是否已经加入 `tiga-ipc`
2. `npm org ls tiga-ipc` 是否能看到当前账号
3. 当前账号是否具备 owner / write 权限

### 3. `npm publish` 通过 dry-run，但正式发布失败

这通常意味着：

- 认证问题
- 组织权限问题
- 2FA / token 策略问题

不是包内容问题。

### 4. `npm run build` 在当前环境失败

如果 `npm run build` 因为 `index.node` 被占用，或 `npx @napi-rs/cli build` 在当前环境失败，请直接使用：

```powershell
cargo build --release
Copy-Item .\target\release\mmap_napi.dll .\index.node -Force
```

## 安全建议

1. 不要把 npm token 写入仓库文件
2. 不要把 token 提交到 `package.json`、脚本或文档中
3. 若 token 在聊天、截图或日志中暴露过，请去 npm 后台立即撤销重建
4. 对外发版优先使用最小权限、可审计的 token 策略
