using System.Globalization;
using Square.Controls;
using Square.Events;
using Square.UI;
using TermSquared.Core;
using ControlText = Square.Controls.Text;

namespace TermSquared.App;

internal static class WorkspaceDialogs
{
    public static Task<string?> PromptAsync(
        Element overlayHost,
        string title,
        string initialValue = "",
        string placeholder = "")
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = CreateDialog(title);
        dialog.Style.Set("height", "176px");
        var input = new Input { Value = initialValue, Placeholder = placeholder };
        input.Style.Set("width", "100%");
        dialog.Children.Add(input);
        dialog.Children.Add(BuildActions("取消", "确定", dialog, completion, () => input.Value.Trim()));
        input.AddEventListener<KeyboardEvent>(StandardEvents.KeyDown, e =>
        {
            if (e.KeyCode != 13) return;
            e.PreventDefault();
            Complete(dialog, completion, input.Value.Trim());
        });
        Attach(overlayHost, dialog, completion);
        return completion.Task;
    }

    public static Task<bool> ConfirmAsync(Element overlayHost, string title, string message)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = CreateDialog(title);
        dialog.Style.Set("width", "480px");
        dialog.Style.Set("height", "240px");
        var body = new ControlText(message) { FontSize = 13 };
        body.Style.Set("color", "#c6d0df");
        body.Style.Set("white-space", "pre-wrap");
        body.Style.Set("overflow-wrap", "anywhere");
        body.Style.Set("width", "100%");
        body.Style.Set("flex", "1");
        body.Style.Set("min-height", "0");
        dialog.Children.Add(body);
        dialog.Children.Add(BuildActions("取消", "确认", dialog, completion, static () => true));
        Attach(overlayHost, dialog, completion);
        return completion.Task;
    }

    public static Task<ConnectionEditorValue?> EditConnectionAsync(
        Element overlayHost,
        ConnectionProfile? profile = null)
    {
        var completion = new TaskCompletionSource<ConnectionEditorValue?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = CreateDialog(profile is null ? "新增 SSH 连接" : "编辑 SSH 连接");
        dialog.Style.Set("width", "480px");
        dialog.Style.Set("height", "520px");
        var form = new View();
        form.ClassList.Add("connection-form");
        form.Style.Set("display", "flex");
        form.Style.Set("flex-direction", "column");
        form.Style.Set("gap", "10px");
        form.Style.Set("height", "370px");
        var name = AddField(form, "连接名称", profile?.Name ?? "", "例如：生产服务器");
        var host = AddField(form, "主机地址", profile?.Host ?? "", "主机名或 IP 地址");
        var port = AddField(form, "端口", (profile?.Port ?? 22).ToString(CultureInfo.InvariantCulture), "22", "number");
        var username = AddField(form, "用户名", profile?.Username ?? "", "SSH 用户名");
        var password = AddField(form, profile is null ? "密码" : "新密码", "",
            profile is null ? "必填；将使用 Windows DPAPI 加密保存" : "留空保持原密码；修改目标时必须重新输入", "password");
        dialog.Children.Add(form);
        var error = new ControlText("") { FontSize = 12 };
        error.Style.Set("color", "#ff9baa");
        error.Style.Set("min-height", "18px");
        dialog.Children.Add(error);

        void Submit()
        {
            error.TextContent = "";
            if (!int.TryParse(port.Value, out var portNumber) || portNumber is < 1 or > 65535)
            {
                error.TextContent = "端口必须是 1 到 65535 之间的整数。";
                return;
            }
            if (string.IsNullOrWhiteSpace(name.Value) || string.IsNullOrWhiteSpace(host.Value) ||
                string.IsNullOrWhiteSpace(username.Value))
            {
                error.TextContent = "连接名称、主机地址和用户名不能为空。";
                return;
            }
            if (profile is null && string.IsNullOrWhiteSpace(password.Value))
            {
                error.TextContent = "新增连接必须输入密码。";
                return;
            }
            Complete(dialog, completion, new ConnectionEditorValue(
                name.Value.Trim(), host.Value.Trim(), portNumber, username.Value.Trim(), password.Value));
        }

        var actions = new View();
        actions.Style.Set("display", "flex");
        actions.Style.Set("flex-direction", "row");
        actions.Style.Set("justify-content", "flex-end");
        actions.Style.Set("gap", "8px");
        actions.Style.Set("height", "36px");
        var cancel = new Button("取消");
        cancel.Style.Set("width", "92px");
        cancel.AddEventListener(StandardEvents.Click, dialog.Close);
        var confirm = new Button(profile is null ? "新增" : "保存");
        confirm.ClassList.Add("button-primary");
        confirm.Style.Set("width", "92px");
        confirm.AddEventListener(StandardEvents.Click, Submit);
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        dialog.Children.Add(actions);
        password.AddEventListener<KeyboardEvent>(StandardEvents.KeyDown, e =>
        {
            if (e.KeyCode != 13) return;
            e.PreventDefault();
            Submit();
        });
        dialog.AddEventListener("close", () => password.Value = "", new AddEventListenerOptions { Once = true });
        Attach(overlayHost, dialog, completion);
        return completion.Task;
    }

    private static Dialog CreateDialog(string title)
    {
        var dialog = new Dialog { CloseOnBackdropClick = false };
        dialog.ClassList.Add("workspace-dialog");
        dialog.Style.Set("display", "flex");
        dialog.Style.Set("flex-direction", "column");
        dialog.Style.Set("width", "420px");
        dialog.Style.Set("height", "176px");
        dialog.Style.Set("padding", "18px");
        dialog.Style.Set("gap", "14px");
        var heading = new ControlText(title) { FontSize = 16 };
        heading.Style.Set("font-weight", "700");
        heading.Style.Set("color", "#f2f6fb");
        dialog.Children.Add(heading);
        return dialog;
    }

    private static Input AddField(
        View form,
        string label,
        string value,
        string placeholder,
        string type = "text")
    {
        var field = new View();
        field.Style.Set("display", "flex");
        field.Style.Set("flex-direction", "column");
        field.Style.Set("gap", "5px");
        field.Style.Set("height", "64px");
        var caption = new ControlText(label) { FontSize = 12 };
        caption.Style.Set("color", "#9eabbc");
        var input = new Input { Value = value, Placeholder = placeholder, Type = type };
        input.Style.Set("width", "100%");
        field.Children.Add(caption);
        field.Children.Add(input);
        form.Children.Add(field);
        return input;
    }

    private static View BuildActions<T>(
        string cancelText,
        string confirmText,
        Dialog dialog,
        TaskCompletionSource<T> completion,
        Func<T> result)
    {
        var actions = new View();
        actions.Style.Set("display", "flex");
        actions.Style.Set("flex-direction", "row");
        actions.Style.Set("justify-content", "flex-end");
        actions.Style.Set("gap", "8px");
        actions.Style.Set("height", "36px");
        actions.Style.Set("flex-shrink", "0");
        var cancel = new Button(cancelText);
        cancel.Style.Set("width", "92px");
        cancel.AddEventListener(StandardEvents.Click, () => dialog.Close());
        var confirm = new Button(confirmText);
        confirm.ClassList.Add("button-primary");
        confirm.Style.Set("width", "92px");
        confirm.AddEventListener(StandardEvents.Click, () => Complete(dialog, completion, result()));
        actions.Children.Add(cancel);
        actions.Children.Add(confirm);
        return actions;
    }

    private static void Attach<T>(Element overlayHost, Dialog dialog, TaskCompletionSource<T> completion)
    {
        dialog.AddEventListener("close", () =>
        {
            if (dialog.ParentNode is Element parent) parent.Children.Remove(dialog);
            completion.TrySetResult(default!);
        }, new AddEventListenerOptions { Once = true });
        overlayHost.Children.Add(dialog);
        dialog.Open();
    }

    private static void Complete<T>(Dialog dialog, TaskCompletionSource<T> completion, T value)
    {
        if (completion.TrySetResult(value)) dialog.Close();
    }
}
