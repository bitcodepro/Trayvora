const vscode = require("vscode");
const net = require("node:net");
const uuid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;

function activate(context) {
    if (process.platform !== "win32") return;
    const pins = new WeakMap();
    const active = () => vscode.window.tabGroups.activeTabGroup.activeTab;
    function target() {
        const tab = active();
        const uri = tab?.input?.uri;
        if (uri && ["openai-codex", "claude-code"].includes(uri.scheme)) {
            const ids = [uri.authority, ...uri.path.split("/")].filter(s => uuid.test(s));
            if (ids.length === 1) return { agent: uri.scheme === "openai-codex" ? "Codex" : "Claude", id: ids[0] };
        }
        return tab ? pins.get(tab) : undefined;
    }
    let listener;
    function sync() {
        if (!vscode.window.state.focused) {
            if (listener) { listener.close(); listener = undefined; }
            return;
        }
        if (listener) return;
        const server = net.createServer(socket => {
            socket.on("error", () => {});
            socket.end(JSON.stringify(vscode.window.state.focused ? target() || {} : {}) + "\n");
        });
        listener = server;
        server.on("error", () => { if (listener === server) listener = undefined; });
        server.listen("\\\\.\\pipe\\trayvora-active-agent-v1");
    }
    const timer = setInterval(sync, 750);
    context.subscriptions.push(vscode.window.onDidChangeWindowState(sync),
        vscode.commands.registerCommand("trayvora.pinSession", async () => {
            const tab = active();
            if (!tab) return;
            const isClaudeEditor = ["claudeVSCodePanel", "mainThreadWebview-claudeVSCodePanel"].includes(tab.input?.viewType);
            const agent = isClaudeEditor ? "Claude" : await vscode.window.showQuickPick(["Codex", "Claude"], { title: "Agent for this tab" });
            if (!agent) return;
            const id = await vscode.window.showInputBox({ title: "Existing chat ID", validateInput: text => uuid.test(text.trim()) ? null : "Enter the chat UUID" });
            if (id) { pins.set(tab, { agent, id: id.trim() }); vscode.window.showInformationMessage("Trayvora binding saved for this tab."); }
        }),
        vscode.commands.registerCommand("trayvora.unpinSession", () => { if (active()) pins.delete(active()); }),
        { dispose() { clearInterval(timer); if (listener) listener.close(); } });
    sync();
}
module.exports = { activate };
