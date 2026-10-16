@echo off
REM ���� Cocoa SDK����׼�⣩��src\Cocoa.SDK\Cocoa.SDK.cosln��System.Core\*.co��-> src\Cocoa.SDK\out\System.Core.coa
REM ��Ŀ¼���ּ��أ�δ���Ĵ�ģ���� System.Net.coa/System.Json.coa
REM  �� Cocoa.SDK.cosln ����Լ��� .coproj �����Զ�������
REM �÷���tools\build-sdk.cmd
setlocal
set "ROOT=%~dp0.."
set "SDK=%ROOT%\src\Cocoa.SDK"
set "OUT=%SDK%\out"

if not exist "%OUT%" mkdir "%OUT%"

dotnet build "%ROOT%\src\Cocoa.Cs\Tools\Cli\Cocoa.Cli" --nologo || exit /b 1
dotnet run --project "%ROOT%\src\Cocoa.Cs\Tools\Cli\Cocoa.Cli" --no-build -- build -p "%SDK%\Cocoa.SDK.cosln" --no-incremental || exit /b 1

REM �ռ��������� src\Cocoa.Cs\libs����⣻Directory.Build.targets ����ʱ�Զ��ַ����� bin��
REM SystemLibrary ����̽�ⶵ�׸��ǹ���ǰ��ȱ��
set "LIBS=%ROOT%\src\Cocoa.Cs\libs"
if not exist "%LIBS%" mkdir "%LIBS%"
REM �ռ�����ģ�飨System.Core + System.Collections��collections �� 6b/M0-1c ������л���
copy /y "%OUT%\System.Core.coa" "%LIBS%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%LIBS%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%LIBS%\System.IO.coa" >nul

REM �ռ��������� tools\cocoa-sdk�����¿����а� SDK ���գ�
set "SDKOUT=%ROOT%\tools\cocoa-sdk"
if not exist "%SDKOUT%" mkdir "%SDKOUT%"
copy /y "%OUT%\System.Core.coa" "%SDKOUT%\System.Core.coa" >nul
if exist "%OUT%\System.Collections.coa" copy /y "%OUT%\System.Collections.coa" "%SDKOUT%\System.Collections.coa" >nul
if exist "%OUT%\System.IO.coa" copy /y "%OUT%\System.IO.coa" "%SDKOUT%\System.IO.coa" >nul

REM �й� dll ��Ԥ���������ѷ������ coa �������ɣ�ProjectBuilder.EnsureManagedDlls��
endlocal
