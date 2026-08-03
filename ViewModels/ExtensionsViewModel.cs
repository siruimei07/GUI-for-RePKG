using System.Collections.ObjectModel;

namespace FieldStation.ViewModels;

public sealed class ExtensionsViewModel
{
    public ObservableCollection<ExtensionSlot> Slots { get; } =
    [
        new("01", "command.secondary", "总控次级仪表", "图表、服务健康或领域摘要。", "LOCAL REGION"),
        new("02", "topology.detail", "拓扑详情附件", "选中节点的业务检查器。", "LOCAL REGION"),
        new("03", "archive.preview", "资产预览器", "文件树、编辑器或媒体预览。", "LOCAL REGION"),
        new("04", "reports.annotation", "报告注释层", "解释、导出或审批操作。", "LOCAL REGION"),
        new("05", "extensions.canvas", "完整模块画布", "独立业务工具或完整编辑器。", "FULL REGION"),
        new("06", "PageRegistry", "整页注册入口", "向全局导航增加独立页面。", "PAGE FACTORY")
    ];
}
