namespace TermSquared.App;

internal static class AppTheme
{
    public const string Css = """
        * {
          font-family: "Segoe UI Variable", "Segoe UI", sans-serif;
        }

        .app-shell {
          background: #0b0f14;
          color: #e6edf6;
        }

        .top-bar {
          background: #111720;
          border-bottom: 1px solid #253040;
          padding: 0 10px;
        }

        .muted {
          color: #8290a3;
        }

        .eyebrow {
          color: #718096;
          font-weight: 700;
          letter-spacing: 0.8px;
        }

        .top-menu {
          background: #111720;
        }

        .top-menu MenuItem {
          color: #aeb9c8;
        }

        .top-menu MenuItem:hover,
        .top-menu MenuItem:open {
          background: #1c2633;
          color: #ffffff;
        }

        .top-menu Menu {
          min-width: 220px;
          background: #151c26;
          border: 1px solid #303c4d;
          border-radius: 8px;
          box-shadow: 0 12px 32px rgba(0, 0, 0, 0.35);
        }

        .top-menu Menu MenuItem {
          background: #151c26;
          color: #d8e0eb;
        }

        .top-menu Menu MenuItem:hover,
        .top-menu Menu MenuItem:open {
          background: #24344a;
          color: #ffffff;
        }

        .top-menu Menu MenuSeparator {
          color: #303c4d;
        }

        .sidebar {
          background: #10161e;
        }

        .panel-header {
          background: #10161e;
          border-bottom: 1px solid #253040;
        }

        .session-tabs {
          background: #10161e;
          border-bottom: 1px solid #253040;
        }

        .panel-title {
          color: #e7edf5;
          font-weight: 700;
          line-height: 20px;
        }

        .fluent-icon {
          font-family: "Segoe Fluent Icons", "Segoe MDL2 Assets";
          font-weight: 400;
        }

        .section-label {
          color: #7d8ba0;
          font-weight: 700;
          letter-spacing: 0.65px;
        }

        .surface {
          background: #111821;
          border: 1px solid #253040;
          border-radius: 10px;
        }

        .subtle-surface {
          background: #0e141c;
          border: 1px solid #202a38;
          border-radius: 8px;
        }

        .success-surface {
          background: #10231d;
          border: 1px solid #225844;
          border-radius: 8px;
        }

        .warning-surface {
          background: #281f11;
          border: 1px solid #6b4d1d;
          border-radius: 8px;
        }

        .status-chip {
          background: #14231e;
          color: #7ee2ae;
          border: 1px solid #24533f;
          border-radius: 999px;
        }

        .count-chip {
          background: #1b2532;
          color: #9cabc0;
          border: 1px solid #2a3748;
          border-radius: 999px;
        }

        Input {
          height: 36px;
          background: #0b1118;
          color: #e7edf6;
          border: 1px solid #334155;
          border-radius: 7px;
        }

        Input:hover {
          border: 1px solid #4a5c72;
        }

        Input:focus {
          border: 1px solid #4f8cff;
          box-shadow: 0 0 0 2px rgba(79, 140, 255, 0.18);
        }

        Button {
          background: #1c2735;
          color: #dfe7f2;
          border: 1px solid #344358;
          border-radius: 7px;
          cursor: pointer;
          flex-shrink: 0;
        }

        Button:focus {
          border: 1px solid #5b94ff;
          box-shadow: 0 0 0 2px rgba(79, 140, 255, 0.18);
        }

        .button-primary {
          background: #3f7be8;
          color: #ffffff;
          border: 1px solid #5a91f2;
        }

        .button-danger {
          background: #332028;
          color: #ffb8c3;
          border: 1px solid #633140;
        }

        .button-quiet {
          background: #151d28;
          color: #b6c1d0;
          border: 1px solid #2a3748;
        }

        .icon-button {
          background: #151d28;
          color: #b8c5d6;
          border: 1px solid #2a3748;
          border-radius: 6px;
        }

        .icon-button:hover {
          background: #213047;
          color: #ffffff;
        }

        .icon-button-primary {
          background: #3f7be8;
          color: #ffffff;
          border: 1px solid #5a91f2;
        }

        .resource-button {
          height: 44px;
          background: #10161e;
          color: #aeb9c8;
          border: 1px solid transparent;
          border-radius: 7px;
        }

        .resource-button:hover {
          background: #172130;
          color: #ffffff;
        }

        .resource-button.selected {
          background: #1c3150;
          color: #dbe9ff;
          border: 1px solid #315b91;
        }

        .not-ready {
          background: #141b24;
          color: #68768a;
          border: 1px solid #26313f;
        }

        .workspace-header {
          background: #111821;
          border-bottom: 1px solid #253040;
        }

        .session-tabs {
          background: #0f151d;
          border-bottom: 1px solid #253040;
        }

        .session-tab {
          background: #182230;
          color: #dfe7f2;
          border: 1px solid #344358;
          border-bottom: 2px solid #4f8cff;
          border-radius: 7px 7px 0 0;
        }

        .session-tab.active {
          background: #20324b;
          color: #ffffff;
        }

        .session-tab-close {
          background: #182230;
          color: #8d9aae;
          border: 1px solid #344358;
          border-left: 0;
          border-radius: 0 7px 0 0;
        }

        .session-tab-close:hover {
          background: #3a2330;
          color: #ffb8c3;
        }

        .session-tab.connecting {
          border-bottom: 2px solid #f6c66b;
        }

        .session-tab.connected {
          border-bottom: 2px solid #4dd394;
        }

        .session-tab.failed {
          border-bottom: 2px solid #ff7f8f;
        }

        .session-tool-tab {
          background: transparent;
          color: #8d9aae;
          border: 0;
          border-bottom: 2px solid transparent;
          border-radius: 5px 5px 0 0;
        }

        .session-tool-tab:hover {
          background: #172130;
          color: #ffffff;
        }

        .session-tool-tab.active {
          background: #172130;
          color: #ffffff;
          border-bottom: 2px solid #4f8cff;
        }

        .workspace-title {
          color: #f2f6fb;
          font-weight: 700;
        }

        .workspace-toolbar {
          background: #0f151d;
          border-bottom: 1px solid #253040;
        }

        .terminal-frame {
          background: #070a0e;
          border: 0;
        }

        .bottom-panel {
          background: #0f151d;
          border-top: 1px solid #253040;
        }

        .command-panel-header {
          background: #111821;
          border-bottom: 1px solid #253040;
        }

        .command-editor {
          font-family: "Cascadia Mono", Consolas, monospace;
          font-size: 13px;
        }

        .sftp-path-bar {
          background: #111821;
          border-bottom: 1px solid #253040;
        }

        .tree-toolbar {
          background: #0f151d;
          border-bottom: 1px solid #202a38;
        }

        .tree-action {
          height: 30px;
          padding: 3px 9px;
          background: #141d28;
          color: #cbd6e5;
          border: 1px solid #304055;
          border-radius: 6px;
        }

        .tree-action:hover {
          background: #20324a;
          color: #ffffff;
        }

        .connection-form Input {
          background: #0f151d;
          color: #e7edf5;
          border: 1px solid #344358;
          border-radius: 6px;
        }

        .connection-tree,
        .sftp-tree {
          background: #10161e;
          color: #bdc8d7;
        }

        .connection-tree TreeItem,
        .sftp-tree TreeItem {
          color: #bdc8d7;
          background: transparent;
          border-radius: 5px;
          font-size: 13px;
        }

        .connection-tree TreeItem:hover,
        .sftp-tree TreeItem:hover {
          background: #172130;
          color: #ffffff;
        }

        .connection-tree TreeItem:checked,
        .sftp-tree TreeItem:checked {
          background: #1c3150;
          color: #e8f1ff;
        }

        .connection-folder,
        .sftp-directory {
          color: #d7e5f9;
          font-weight: 600;
        }

        .sftp-file {
          font-family: "Cascadia Mono", Consolas, monospace;
          color: #b8c4d4;
        }

        .tree-placeholder {
          color: #718096;
          font-style: italic;
        }

        .context-menu {
          min-width: 220px;
          background: #151c26;
          border: 1px solid #303c4d;
          border-radius: 8px;
          box-shadow: 0 12px 32px rgba(0, 0, 0, 0.35);
        }

        .context-menu MenuItem {
          background: #151c26;
          color: #d8e0eb;
        }

        .context-menu MenuItem:hover,
        .context-menu MenuItem:open {
          background: #24344a;
          color: #ffffff;
        }

        .workspace-dialog {
          background: #151c26;
          color: #e7edf5;
          border: 1px solid #3a4658;
          border-radius: 10px;
          box-shadow: 0 18px 48px rgba(0, 0, 0, 0.5);
        }

        .history-item {
          background: #111821;
          border: 1px solid #253040;
          border-radius: 8px;
        }

        .status-bar {
          background: #0d131a;
          border-top: 1px solid #253040;
        }

        .splitter {
          background: #10161e;
        }

        .splitter:hover,
        .splitter:active {
          background: #3f7be8;
        }
        """;
}
