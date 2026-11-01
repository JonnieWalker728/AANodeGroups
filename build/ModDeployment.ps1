#Requires -Version 5.1
# 兼容 Windows PowerShell 5.1 与 PowerShell 7；本文件保留 UTF-8 BOM 以正确读取中文注释。
# 发行安装和临时测试部署共用此锁；调用方须持有返回的句柄直到部署/恢复全部完成。
# 锁的生命周期由文件句柄决定，残留的空锁文件不会阻止下次使用。
function Enter-NodeGroupsDeployment {
    <#
    .SYNOPSIS
    获取当前仓库的独占部署锁，阻止同仓库安装与测试同时覆盖游戏中的模组。
    .PARAMETER RepositoryDirectory
    仓库根目录；锁文件存放在其 TestResults 目录内。
    .OUTPUTS
    System.IO.FileStream。调用方必须在 finally 中 Dispose，以释放独占访问。
    #>
    param([Parameter(Mandatory)][string]$RepositoryDirectory)
    $lockDirectory = Join-Path $RepositoryDirectory 'TestResults'
    [IO.Directory]::CreateDirectory($lockDirectory) | Out-Null
    try {
        return [IO.File]::Open((Join-Path $lockDirectory 'mod-deployment.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw "Another NodeGroups test or installation owns the deployment lock: $($_.Exception.Message)"
    }
}
