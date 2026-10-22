; ============================================================================
;  ZungMail 安装包脚本（NSIS 3 / Unicode）
;
;  通常不需要直接调用本文件，用 packaging\build-installer.ps1 一键构建：
;      .\packaging\build-installer.ps1
;
;  脚本由构建脚本通过 /D 传入以下变量：
;      VERSION        版本号（三段数字，如 1.0.0）
;      SOURCE         dotnet publish 输出目录
;      OUTPUT         安装包输出路径
;      ICON           应用图标（.ico）
;      APPSIZE_KB     安装后占用空间（KB，用于「应用和功能」里的显示）
;      FRAMEWORK_DEPENDENT  1 = 框架依赖版（额外检测 .NET 8 桌面运行时）
; ============================================================================

Unicode true
SetCompressor /SOLID lzma
SetCompressorDictSize 64

!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

; ---------------- 参数默认值（直接 makensis 时可独立跑通）----------------
!ifndef VERSION
  !define VERSION "1.0.0"
!endif
!ifndef SOURCE
  !define SOURCE "..\packaging\obj\publish"
!endif
!ifndef OUTPUT
  !define OUTPUT "..\dist\ZungMail-setup.exe"
!endif
!ifndef ICON
  !define ICON "..\src\ZungMail.App\Assets\Brand\ZungMail.ico"
!endif
!ifndef APPSIZE_KB
  !define APPSIZE_KB "0"
!endif
!ifndef FRAMEWORK_DEPENDENT
  !define FRAMEWORK_DEPENDENT "0"
!endif

; ---------------- 产品信息 ----------------
!define APP_NAME        "ZungMail"
!define APP_EXE         "ZungMail.exe"
!define APP_PUBLISHER   "Leung"
!define UNINST_KEY      "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_NAME}"
!define WEBVIEW2_URL    "https://go.microsoft.com/fwlink/p/?LinkId=2124703"
!define DOTNET_URL      "https://dotnet.microsoft.com/download/dotnet/8.0"

; 改名前的旧版（MailClient）：旧安装包也是 NSIS 做的，卸载信息、安装目录、开始菜单与
; 桌面快捷方式全是旧名字。新版安装时会一次性清掉这份旧 NSIS 安装，否则「应用和功能」里
; 会同时躺着 MailClient 与 ZungMail 两个条目，磁盘上还会留一份旧程序。
!define LEGACY_APP_NAME    "MailClient"
!define LEGACY_APP_EXE     "MailClient.exe"
!define LEGACY_UNINST_KEY  "Software\Microsoft\Windows\CurrentVersion\Uninstall\${LEGACY_APP_NAME}"

Name "${APP_NAME} ${VERSION}"
OutFile "${OUTPUT}"
InstallDir "$PROGRAMFILES64\${APP_NAME}"
InstallDirRegKey HKLM "${UNINST_KEY}" "InstallLocation"
RequestExecutionLevel admin
BrandingText "${APP_NAME} ${VERSION} 安装程序"

VIProductVersion "${VERSION}.0"
VIAddVersionKey /LANG=2052 "ProductName"      "${APP_NAME}"
VIAddVersionKey /LANG=2052 "FileDescription"  "${APP_NAME} 安装程序"
VIAddVersionKey /LANG=2052 "FileVersion"      "${VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion"   "${VERSION}"
VIAddVersionKey /LANG=2052 "CompanyName"      "${APP_PUBLISHER}"
VIAddVersionKey /LANG=2052 "LegalCopyright"   "Copyright © ${APP_PUBLISHER}"

; ---------------- 界面 ----------------
!define MUI_ICON   "${ICON}"
!define MUI_UNICON "${ICON}"
!define MUI_ABORTWARNING
!define MUI_ABORTWARNING_TEXT "确定要取消安装 ${APP_NAME} 吗？"

!define MUI_WELCOMEPAGE_TITLE "安装 ${APP_NAME} ${VERSION}"
!define MUI_WELCOMEPAGE_TEXT "将在这台电脑上安装 ${APP_NAME} 邮件客户端。$\r$\n$\r$\n安装程序会先关闭正在运行的 ZungMail（未发送的草稿已自动保存），随后复制程序文件并创建快捷方式。$\r$\n$\r$\n点「下一步」继续。"

!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "立即运行 ${APP_NAME}"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

; ---------------- 安装前的环境检查 ----------------
Function .onInit
  ; 只支持 64 位系统（应用以 win-x64 发布）
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "ZungMail 只支持 64 位 Windows 10 / 11。"
    Abort
  ${EndIf}
  SetRegView 64

  ; WebView2 运行时缺失时正文会退化为纯文本，提示但不阻止安装。
  ; 用 FindFirst 探测目录（机器级 / 用户级两种安装位置），比 IfFileExists 通配更可靠。
  StrCpy $2 ""
  FindFirst $0 $1 "$PROGRAMFILES32\Microsoft\EdgeWebView\Application\*.*"
  ${If} $0 != ""
    StrCpy $2 "1"
    FindClose $0
  ${EndIf}
  ${If} $2 == ""
    FindFirst $0 $1 "$LOCALAPPDATA\Microsoft\EdgeWebView\Application\*.*"
    ${If} $0 != ""
      StrCpy $2 "1"
      FindClose $0
    ${EndIf}
  ${EndIf}
  ${If} $2 == ""
    MessageBox MB_YESNO|MB_ICONEXCLAMATION \
      "未检测到 Microsoft Edge WebView2 运行时。$\r$\n$\r$\n缺少它时邮件正文会退化为纯文本（其余功能不受影响）。$\r$\n可先安装 WebView2 运行时再使用，或点「是」继续安装。$\r$\n$\r$\n仍然继续吗？" \
      IDYES +2
    Abort
  ${EndIf}

  ; 框架依赖版额外要求已装 .NET 8 桌面运行时
  ${If} "${FRAMEWORK_DEPENDENT}" == "1"
    StrCpy $2 ""
    FindFirst $0 $1 "$PROGRAMFILES64\dotnet\shared\Microsoft.WindowsDesktop.App\8.*"
    ${If} $0 != ""
      StrCpy $2 "1"
      FindClose $0
    ${EndIf}
    ${If} $2 == ""
      MessageBox MB_YESNO|MB_ICONEXCLAMATION \
        "这是依赖 .NET 运行时的版本，但本机没有检测到 .NET 8 桌面运行时。$\r$\n$\r$\n请先安装 .NET 8 Desktop Runtime，否则程序无法启动。$\r$\n下载地址：${DOTNET_URL}$\r$\n$\r$\n仍然继续安装吗？" \
        IDYES +2
      Abort
    ${EndIf}
  ${EndIf}
FunctionEnd

; ---------------- 安装 ----------------
Section "ZungMail 主程序" SEC_MAIN
  SectionIn RO
  SetShellVarContext all
  SetRegView 64

  ; 覆盖安装：先结束正在运行的实例，否则文件被占用会写不进去
  nsExec::Exec 'taskkill /IM ${APP_EXE}'
  Sleep 1200
  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Sleep 400

  ; ---------------- 清理改名前的旧版 MailClient（旧 NSIS 安装）----------------
  ; 旧版主程序可能还开着（用户从旧版本升级上来时就是这样），先结束它。
  nsExec::ExecToLog 'taskkill /IM ${LEGACY_APP_EXE} /F'
  Sleep 400

  ReadRegStr $0 HKLM "${LEGACY_UNINST_KEY}" "DisplayName"
  ${If} $0 != ""
    DetailPrint "发现旧版 MailClient，正在清理旧安装…"
    ; 直接删卸载键：不调用旧卸载器，避免它弹出「是否删除邮件数据」把用户的库删掉
    DeleteRegKey HKLM "${LEGACY_UNINST_KEY}"
    Delete "$SMPROGRAMS\${LEGACY_APP_NAME}\*.lnk"
    RMDir "$SMPROGRAMS\${LEGACY_APP_NAME}"
    Delete "$DESKTOP\${LEGACY_APP_NAME}.lnk"
    ; 只有确认目录里确实是旧版主程序时才整目录删除，避免误删同名的用户目录
    ${If} ${FileExists} "$PROGRAMFILES64\${LEGACY_APP_NAME}\${LEGACY_APP_EXE}"
      RMDir /r "$PROGRAMFILES64\${LEGACY_APP_NAME}"
    ${EndIf}
  ${EndIf}
  ; 注意：这里绝不碰 $APPDATA\${LEGACY_APP_NAME} 与 $LOCALAPPDATA\${LEGACY_APP_NAME}，
  ; 那里可能有旧数据备份；用户数据只在客户端首次启动时做接管或保留。

  SetOutPath "$INSTDIR"
  File /r "${SOURCE}\*.*"

  ; 程序组与卸载器
  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  WriteUninstaller "$INSTDIR\Uninstall.exe"

  ; 卸载信息（「应用和功能」里显示）
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayName"     "${APP_NAME}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayVersion"  "${VERSION}"
  WriteRegStr HKLM "${UNINST_KEY}" "DisplayIcon"     "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKLM "${UNINST_KEY}" "Publisher"       "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINST_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINST_KEY}" "NoRepair" 1

  ${If} ${APPSIZE_KB} > 0
    StrCpy $0 ${APPSIZE_KB}
  ${Else}
    ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  ${EndIf}
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINST_KEY}" "EstimatedSize" "$0"

  ; ---------------- 安装完成后自检 + 自动拉起客户端 ----------------
  ; 先确认主程序还在：360 安全卫士 / 天擎、火绒、腾讯电脑管家这类启发式引擎，
  ; 经常把未签名（或刚落地）的 ZungMail.exe 直接隔离——文件被删后 MSI 会连带报
  ; 「警告 1909 无法创建快捷方式」，用户看到的是一堆看不懂的错误。这里给出可执行的处理办法。
  ; 客户端内「立即安装」会用 /S 静默启动本安装包：装完自动把 ZungMail 打开，
  ; 用户侧就是「点一下 → 自动关 → 装好 → 自动回来」。
  ; 交互安装（双击 setup.exe）不在这里启动，向导「完成」页的「立即运行 ZungMail」已经覆盖。
  ; 用 explorer.exe 转发启动：安装器是管理员权限，直接 Exec 会让客户端继承管理员令牌，
  ; 提权进程会被 UIPI 挡住拖放（从 Outlook 拖邮件、往桌面拖附件都会失效）。
  ${IfNot} ${FileExists} "$INSTDIR\${APP_EXE}"
    MessageBox MB_ICONSTOP|MB_OK \
      "ZungMail 安装未完成：$INSTDIR\${APP_EXE} 不存在。$\r$\n$\r$\n最常见的原因是安全软件把主程序当成可疑文件隔离了（360 安全卫士 / 360 天擎、火绒、腾讯电脑管家等）。$\r$\n请先把安装目录 $INSTDIR 加入安全软件的信任区，再重新运行安装包。$\r$\n$\r$\n也可以改用 MSI 安装包，或联系管理员下发放行策略。"
    SetErrorLevel 1
    Quit
  ${EndIf}

  ${If} ${Silent}
    Exec '"$WINDIR\explorer.exe" "$INSTDIR\${APP_EXE}"'
  ${EndIf}
SectionEnd

Section "桌面快捷方式" SEC_DESKTOP
  SetShellVarContext all
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_MAIN}    "ZungMail 主程序与运行库（必选）。"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "在当前用户桌面创建快捷方式。"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

; ---------------- 卸载 ----------------
Section "Uninstall"
  SetShellVarContext all
  SetRegView 64

  nsExec::Exec 'taskkill /IM ${APP_EXE} /F'
  Sleep 400

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\卸载 ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  ; 只有确认是本程序目录时才整目录删除，避免用户手滑把 INSTDIR 指到别处
  ${If} ${FileExists} "$INSTDIR\${APP_EXE}"
    RMDir /r "$INSTDIR"
  ${Else}
    Delete "$INSTDIR\Uninstall.exe"
  ${EndIf}

  DeleteRegKey HKLM "${UNINST_KEY}"

  ; 本地邮件库与设置：默认保留，卸载器询问后才删
  MessageBox MB_YESNO|MB_ICONQUESTION \
    "是否同时删除本机的邮件数据库、附件缓存与设置？$\r$\n$\r$\n位置：$APPDATA\ZungMail$\r$\n　　　$LOCALAPPDATA\ZungMail$\r$\n$\r$\n选「否」会保留这些数据，重新安装后可继续使用。$\r$\n$\r$\n注意：改名前的旧版 MailClient 目录（$APPDATA\MailClient、$LOCALAPPDATA\MailClient）不会被自动删除，需要时请手动清理。" \
    IDNO keepUserData
  RMDir /r "$APPDATA\ZungMail"
  RMDir /r "$LOCALAPPDATA\ZungMail"
  keepUserData:
SectionEnd
