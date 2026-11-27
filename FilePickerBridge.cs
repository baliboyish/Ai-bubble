using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

// Keep file selection in the host when the runtime's native chooser does not appear.
// CDP supplies the exact requesting input; never guess a selector or replace page handlers.
internal sealed class FilePickerBridge
{
    readonly CoreWebView2 core;
    readonly Form owner;
    readonly string providerName;
    readonly Action<string> log;
    readonly Func<bool, bool, string[]> choose;
    readonly JavaScriptSerializer json = new JavaScriptSerializer();
    CoreWebView2DevToolsProtocolEventReceiver receiver;
    bool pending;

    internal FilePickerBridge(CoreWebView2 core, Form owner, Action<string> log,
        Func<bool, bool, string[]> choose = null, string providerName = "Muse")
    {
        this.core = core; this.owner = owner; this.log = log;
        this.providerName = providerName;
        this.choose = choose;
    }

    internal async Task EnableAsync()
    {
        log("Host file chooser enabling");
        receiver = core.GetDevToolsProtocolEventReceiver("Page.fileChooserOpened");
        receiver.DevToolsProtocolEventReceived += OnFileChooser;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
            await core.CallDevToolsProtocolMethodAsync("Page.setInterceptFileChooserDialog", "{\"enabled\":true}");
            log("Host file chooser enabled");
        }
        catch
        {
            receiver.DevToolsProtocolEventReceived -= OnFileChooser;
            throw;
        }
    }

    void OnFileChooser(object sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        log("File chooser event received");
        string payload = e.ParameterObjectAsJson;
        // Return from the WebView2 callback before starting a modal message loop.
        if (!owner.IsDisposed && owner.IsHandleCreated)
            owner.BeginInvoke(new Action(async delegate { await HandleAsync(payload); }));
    }

    async Task HandleAsync(string payload)
    {
        if (pending || owner.IsDisposed) return;
        pending = true;
        try { await HandleSelectionAsync(payload); }
        finally { pending = false; }
    }

    async Task HandleSelectionAsync(string payload)
    {
        bool guardStarted = false, restoreTopMost = false, wasTopMost = false;
        Task cleanup = null;
        Exception failure = null;
        string operation = "parse chooser event";
        try
        {
            var request = json.Deserialize<Dictionary<string, object>>(payload);
            object node;
            if (!request.TryGetValue("backendNodeId", out node))
                throw new InvalidOperationException("The file chooser did not identify its input.");
            var target = new Dictionary<string, object> { { "backendNodeId", node } };
            operation = "DOM.describeNode";
            if (providerName == "Claude")
                log("Before DOM.describeNode; backendNodeId=" + node + "; valueType=" + node.GetType().FullName);
            var description = json.Deserialize<Dictionary<string, object>>(
                await core.CallDevToolsProtocolMethodAsync("DOM.describeNode", json.Serialize(target)));
            var input = (Dictionary<string, object>)description["node"];
            if (providerName == "Claude")
                log("DOM.describeNode succeeded; nodeName=" + input["nodeName"] + "; backendNodeId=" + input["backendNodeId"]);
            bool directory = false;
            object attributes;
            if (input.TryGetValue("attributes", out attributes))
            {
                System.Collections.IList pairs = (System.Collections.IList)attributes;
                for (int i = 0; i + 1 < pairs.Count; i += 2)
                    if ((string)pairs[i] == "webkitdirectory") directory = true;
            }
            bool multiple = (string)request["mode"] == "selectMultiple";
            log("File chooser requested; multiple=" + multiple + "; directory=" + directory);
            operation = "choose files";
            string[] files;
            if (choose != null)
            {
                files = choose(multiple, directory);
            }
            else
            {
                if (providerName == "Claude" && !owner.IsDisposed)
                {
                    guardStarted = true;
                    await SetPickingAsync(true);
                }
                if (owner.IsDisposed)
                {
                    files = null;
                }
                else
                {
                    wasTopMost = owner.TopMost;
                    restoreTopMost = true;
                    if (wasTopMost) owner.TopMost = false;
                    files = ChooseFiles(multiple, directory);
                }
            }
            if (files == null || owner.IsDisposed)
            {
                log("File chooser cancelled");
            }
            else
            {
                if (providerName == "Claude")
                {
                    ProbeFileArguments(files);
                    await ProbeInputStateAsync(node);
                }
                target["files"] = files;
                operation = "DOM.setFileInputFiles";
                if (providerName == "Claude")
                    log("Before DOM.setFileInputFiles; backendNodeId=" + node + "; fileCount=" + files.Length);
                await core.CallDevToolsProtocolMethodAsync("DOM.setFileInputFiles", json.Serialize(target));
                log("File selection delivered; count=" + files.Length);
            }
        }
        catch (Exception ex)
        {
            if (providerName == "Claude")
                log("Host file chooser failed at " + operation + "; HResult=0x" + ex.HResult.ToString("X8") + ": " + ex);
            else log("Host file chooser failed: " + ex);
            failure = ex;
        }
        finally
        {
            if (guardStarted) cleanup = SetPickingAsync(false);
            if (restoreTopMost && !owner.IsDisposed) owner.TopMost = wasTopMost;
        }
        if (cleanup != null) await cleanup;
        if (failure != null && !owner.IsDisposed)
            MessageBox.Show(owner, "Could not attach the selected file: " + failure.Message,
                providerName, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    const string BeginPicking = @"
(() => {
    if (window.__aibubbleStopPicking) window.__aibubbleStopPicking();
    const targets = [window, document];
    const events = ['blur', 'focusout'];
    const suppress = event => {
        if (window.__aibubblePicking) event.stopImmediatePropagation();
    };
    window.__aibubblePicking = true;
    window.__aibubbleStopPicking = () => {
        window.__aibubblePicking = false;
        for (const target of targets)
            for (const type of events) target.removeEventListener(type, suppress, true);
        delete window.__aibubbleStopPicking;
    };
    for (const target of targets)
        for (const type of events) target.addEventListener(type, suppress, { capture: true, once: true });
})();
";
    const string EndPicking =
        "window.__aibubblePicking = false; if (window.__aibubbleStopPicking) window.__aibubbleStopPicking();";

    async Task SetPickingAsync(bool picking)
    {
        try
        {
            var response = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", json.Serialize(
                new Dictionary<string, object> { { "expression", picking ? BeginPicking : EndPicking } }));
            if (json.Deserialize<Dictionary<string, object>>(response).ContainsKey("exceptionDetails"))
                log("Claude file picker focus guard script failed");
        }
        catch (Exception ex) { log("Claude file picker focus guard failed: " + ex.Message); }
    }


    void ProbeFileArguments(string[] files)
    {
        try
        {
            bool absolute = true, exists = true, nonEmpty = true;
            foreach (string path in files)
            {
                if (String.IsNullOrWhiteSpace(path)) { nonEmpty = false; absolute = exists = false; continue; }
                absolute &= Path.IsPathRooted(path);
                exists &= File.Exists(path) || Directory.Exists(path);
            }
            // Deliberately omit file paths, names and contents from diagnostics.
            log("File arguments; arrayType=" + files.GetType().FullName + "; count=" + files.Length +
                "; allNonEmpty=" + nonEmpty + "; allAbsolute=" + absolute + "; allPathsExist=" + exists);
        }
        catch (Exception ex) { log("File argument inspection failed: " + ex.Message); }
    }

    async Task ProbeInputStateAsync(object backendNodeId)
    {
        string objectId = null;
        try
        {
            var resolution = json.Deserialize<Dictionary<string, object>>(
                await core.CallDevToolsProtocolMethodAsync("DOM.resolveNode",
                    json.Serialize(new Dictionary<string, object> { { "backendNodeId", backendNodeId } })));
            var remote = (Dictionary<string, object>)resolution["object"];
            objectId = (string)remote["objectId"];
            var arguments = new Dictionary<string, object> {
                { "objectId", objectId }, { "returnByValue", true },
                { "functionDeclaration", "function(){return {connected:this.isConnected,tag:this.tagName,type:this.type,fileCount:this.files?this.files.length:null};}" }
            };
            var inspection = json.Deserialize<Dictionary<string, object>>(
                await core.CallDevToolsProtocolMethodAsync("Runtime.callFunctionOn", json.Serialize(arguments)));
            var result = (Dictionary<string, object>)inspection["result"];
            object value;
            if (result.TryGetValue("value", out value))
                log("Original input after selection: " + json.Serialize(value));
            else log("Original input inspection returned no value");
        }
        catch (Exception ex)
        {
            log("Original input inspection failed; HResult=0x" + ex.HResult.ToString("X8") + ": " + ex.Message);
        }
        if (objectId != null)
        {
            try { await core.CallDevToolsProtocolMethodAsync("Runtime.releaseObject",
                json.Serialize(new Dictionary<string, object> { { "objectId", objectId } })); }
            catch { }
        }
    }

    string[] ChooseFiles(bool multiple, bool directory)
    {
        if (directory)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose a folder to attach";
                return dialog.ShowDialog(owner) == DialogResult.OK ? new[] { dialog.SelectedPath } : null;
            }
        }

        try { return ShowFilePicker(multiple, true); }
        catch (Exception ex)
        {
            log("Modern file picker failed, trying classic picker: " + ex);
            return owner.IsDisposed ? null : ShowFilePicker(multiple, false);
        }
    }

    string[] ShowFilePicker(bool multiple, bool modern)
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "Attach files to " + providerName;
            dialog.Multiselect = multiple;
            dialog.Filter = "All files (*.*)|*.*";
            dialog.CheckFileExists = true;
            dialog.RestoreDirectory = true;
            dialog.AutoUpgradeEnabled = modern;
            return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileNames : null;
        }
    }
}
