# 发布到 GitHub

这个仓库已经提交好并打了 `v1.2.0` 标签，只差"推到你的 GitHub 账号"这一步。
推送需要**你账号的授权**——别人无法代你推送，任何声称能"代发到你账号"的服务本质上都是要你的 token。

下面三种方式任选其一。

## 方式 1：设备码登录（推荐，不用给我密码）

在你的终端里执行：

```powershell
cd D:\POE2\lx-source-downloader
gh auth login --web --git-protocol https
```

终端会显示一个一次性代码（形如 `A1B2-C3D4`）并尝试打开浏览器。**浏览器打不开 GitHub 没关系**：
用手机的移动网络打开 <https://github.com/login/device>，输入那个代码并授权即可。

授权完成后，回到终端执行：

```powershell
pwsh ./publish.ps1
```

脚本会自动建仓库、推送 `main` 和标签。

## 方式 2：用 Personal Access Token

1. 在能打开 GitHub 的设备上访问 <https://github.com/settings/tokens?type=beta>（Fine-grained token）
2. 新建 token：**Repository access → Only select repositories**（先选一个占位或允许全部），
   Permissions 勾 **Contents: Read and write** 和 **Administration: Read and write**（建仓库需要）
3. 有效期设 7 天，生成后复制

然后：

```powershell
$env:GH_TOKEN = '粘贴你的token'
pwsh ./publish.ps1
Remove-Item Env:\GH_TOKEN
```

> token 等于账号的部分权限，用完请到 GitHub 设置里立即 Revoke，不要在聊天窗口贴。

## 方式 3：完全离线，换台能上网的机器推

把这三样拷走：整个仓库目录（或 `.git` 目录即可），以及 `dist/lx-source-downloader.bundle`。

在能上网的机器上：

```bash
git clone lx-source-downloader.bundle lx-source-downloader
cd lx-source-downloader
git remote add origin https://github.com/<你的用户名>/lx-source-downloader.git
git push -u origin main
git push --tags origin
```

（先在 GitHub 网页上建一个空仓库，不要勾选 README/gitignore。）

## 网络排查

当前这台机器实测 `git` 协议是通的（`git ls-remote https://github.com/lyswhut/lx-music-desktop` 正常），
但浏览器可能因 DNS/SNI 间歇失败。如果 push 卡住：

```powershell
# 有本地代理时（Clash 默认 7890，v2rayN 默认 10809）
pwsh ./publish.ps1 -Proxy http://127.0.0.1:7890

# 或者全局给 git 设代理
git config --global http.proxy http://127.0.0.1:7890
git config --global --unset http.proxy   # 用完删掉
```

其它可试：

- 手机热点换一条出口线路（移动网络访问 GitHub 通常比家宽稳定）
- `git -c http.version=HTTP/1.1 push`（某些中间设备对 HTTP/2 处理有问题）
- `git -c http.postBuffer=524288000 push`
- 改 DNS 为 `1.1.1.1` / `8.8.8.8`，或 `ipconfig /flushdns`

## 推送之后

- 仓库 **Settings → Topics** 填 `lx-music, winforms, csharp, windows, music`
- **Actions** 标签页确认 `build` 工作流已启用（首次推送会自动跑一次构建）
- 标签 `v1.2.0` 会触发 Release 工作流，自动生成 `LxSourceDownloader-win-x64.zip`
- 若你的用户名不是 `maygo88`，记得改 `README.md` / `README.zh-CN.md` 里的徽章地址
