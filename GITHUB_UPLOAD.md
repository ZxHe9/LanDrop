# 上传到 GitHub

这个目录本身就是一个完整的仓库内容，不需要再改目录结构。  
`dist/`、`release/`、`*.exe` 已被 `.gitignore` 排除，仓库只保存源码和资源；GitHub Actions 会自动编译出 exe。

下面三种方式选一种即可。

---

## 方式一：GitHub 网页上传（最简单，不用装 Git）

1. 登录 GitHub，点右上角 **+** → **New repository**。
2. 仓库名建议填 `LanDrop`，选择 Public 或 Private。
3. **不要**勾选 “Add a README file”（我们已经有 README）。
4. 创建后，在仓库页面点 **Add file** → **Upload files**。
5. 解压 `LanDrop-source-v1.0.0.zip`，把解压出来的**所有文件和文件夹**拖进上传区域：
   - `.github/`
   - `assets/`
   - `docs/`
   - `src/`
   - `web/`
   - `app.manifest`
   - `build.ps1`
   - `README.md`
   - `使用说明.txt`
   - `LICENSE`
   - 等等
6. 下方 Commit message 填 `Initial commit: LanDrop v1.0.0`，点 **Commit changes**。

> GitHub 网页上传支持文件夹，直接把整个解压目录拖进去即可。  
> `.github` 是隐藏文件夹，如果拖拽时被系统隐藏，请确认它也被上传，否则 Actions 不会出现。

---

## 方式二：Git 命令行

先安装 [Git for Windows](https://git-scm.com/download/win)，然后：

```powershell
# 进入解压后的源码目录
cd LanDrop-source

git init -b main
git add .
git commit -m "Initial commit: LanDrop v1.0.0"

# 在 GitHub 新建空仓库后，把地址换成你自己的
git remote add origin https://github.com/你的用户名/LanDrop.git
git push -u origin main
```

如果 GitHub 默认分支叫 `master`，把上面的 `main` 改成 `master` 即可。

---

## 方式三：GitHub Desktop

1. 安装 GitHub Desktop，登录账号。
2. **File** → **Add local repository**，选择解压后的源码目录。
3. 如果提示 “not a Git repository”，点 **create a repository**，不要勾选初始化 README。
4. 填写 Summary：`Initial commit: LanDrop v1.0.0`，点 **Commit to main**。
5. 点 **Publish repository**，选择 Public/Private，发布。

---

## 上传后：自动编译 exe

仓库已经包含 `.github/workflows/build.yml`。

1. 打开仓库的 **Actions** 标签页。
2. 左侧选择 **Build Windows executable**。
3. 点 **Run workflow** → **Run workflow**（也可以等 push 自动触发）。
4. 运行完成后，点进这次运行，在页面底部 **Artifacts** 下载：
   - `LanDrop-windows.zip`

里面就是编译好的 `LanDrop-1.0-Windows.zip`（含 exe、README、使用说明）。

---

## 发布 Release（可选）

1. 仓库右侧 **Releases** → **Create a new release**。
2. Tag 填 `v1.0.0`，标题填 `LanDrop v1.0.0`。
3. 把本地 `build.ps1` 生成的 `release/LanDrop-1.0-Windows.zip`，或 Actions 下载的 artifact，拖到附件区域。
4. 点 **Publish release**。

---

## 常见问题

### 上传后 Actions 编译失败？

- 确认 `.github/workflows/build.yml` 已经上传；
- 打开 Actions 日志，看是不是 `csc.exe` 路径问题；
- 本项目使用 Windows 自带的 .NET Framework 4.8 编译器，`windows-latest` 运行器默认具备；
- 也可以本地执行 `powershell -ExecutionPolicy Bypass -File .\build.ps1` 验证。

### 要不要把 exe 提交进仓库？

不建议。`.gitignore` 已经排除了 `dist/` 和 `release/`。  
源码仓库保持干净，exe 通过 GitHub Actions 或 Release 提供。

### 中文文件名会不会有问题？

GitHub、Git for Windows 和 Windows 10/11 都支持 UTF-8 中文文件名。  
如果命令行显示乱码，执行：

```powershell
git config --global core.quotepath false
```

### 仓库名可以换吗？

可以。`build.ps1` 和 workflow 都不依赖仓库名，只依赖当前目录结构。
