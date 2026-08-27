@echo off
REM Build Cocoa SDK (stdlib): src\Cocoa.SDK\Cocoa.SDK.cosln (System.Core\*.co) -> src\Cocoa.SDK\out\System.Core.cod
REM (Directory discovery loading: future big modules like System.Net.cod/System.Json.cod
REM  add their own .coproj to Cocoa.SDK.cosln and get built automatically)
REM Usage: tools\build-sdk.cmd
setlocal
set "ROOT=%~dp0.."
set "SDK=%ROOT%\src\Cocoa.SDK"
set "OUT=%SDK%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Cocoa.Compiler" --no-build -- build -p "%SDK%\Cocoa.SDK.cosln" --no-incremental || exit /b 1

REM Collect to central store src\Cocoa.Cs\libs (committed; Directory.Build.targets fans out
REM to project bins on build, SystemLibrary walk-up probe covers pre-build gap)
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
copy /y "%OUT%\System.Core.cod" "%LIBS%\System.Core.cod" >nul
REM 托管 dll 不预生成：消费方构建时按需从 cod 再生（lazy，见 ProjectBuilder.EnsureManagedDlls）
REM 注：System.Collections 等泛型密集模块（List<T>/Dictionary<K,V> 含 `new T[]`）当前 .cod 序列化
REM 尚不支持开放泛型数组创建（G7 待补），暂以“源码方式”集成（见 CollectionFacadeTests），不纳入 .cod 构建。

echo System.Core.cod built: %OUT%\System.Core.cod (collected to src\Cocoa.Cs\libs)
endlocal
