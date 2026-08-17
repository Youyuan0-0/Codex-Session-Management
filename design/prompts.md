# Codex 会话热同步 UI 提示词

生成模型：`gpt-image-2`

## 统一视觉系统

```text
Use case: ui-mockup
Asset type: high-fidelity Windows desktop application screen
Product: "Codex 会话热同步", a Chinese Windows utility that synchronizes JSONL session metadata, session_index.jsonl, and two SQLite databases.
Visual thesis: a quiet technical data-consistency workbench, not a generic dashboard.
Canvas: landscape desktop app window, approximately 1440x900, native Windows title bar, all content visible without scrolling.
Composition: compact product header; restrained command strip for Codex Home, target Provider, archived-session toggle, and automatic-backup indicator; one dominant synchronization topology showing JSONL flowing to two SQLite databases; one full-width status/action rail; calm monospaced execution log at the bottom.
Style: realistic production UI, crisp Simplified Chinese typography, Windows Fluent-inspired but custom and premium; dense, calm, utilitarian, strong alignment and whitespace.
Palette: cool white and very light neutral gray, charcoal text, one teal-green action accent, restrained cyan technical details, amber only for warnings.
Controls: familiar outline icons, 6px corner radius maximum, subtle 1px dividers, minimal shadows.
Avoid: purple, beige, gradients, marketing layout, card mosaic, nested cards, fake charts, illustrations, extra navigation, pill soup, watermark, unrelated logos.
```

## 01 默认 / 待同步

```text
Preserve the shared visual system.
Show "JSONL 会话" value "268" and "索引值: 184" on the left.
Show a central circular sync action with "632 项待处理".
Show "根目录 state_5.sqlite" value "268/268" and "sqlite/state_5.sqlite" value "221/221" on the right, with missing/path/provider diagnostics.
Status rail: "检测到 632 项差异，可以立即同步".
Actions: "立即同步", "刷新状态", "打开备份目录".
Bottom title: "执行日志" with one timestamped status line.
```

## 02 同步中

```text
Use the default screen as an exact layout and styling reference; change only operating state.
Disable and mute configuration and secondary controls.
Central active progress ring: "68%", label "正在事务同步 SQLite".
Animated-looking data dashes flow from JSONL toward both SQLite destinations.
Status rail: "正在同步：已处理 182 / 268 个会话".
Primary button: disabled "同步中…".
Log lines:
"[19:37:02] 已创建一致性备份"
"[19:37:03] JSONL 元数据更新完成"
"[19:37:04] 正在事务同步两份 SQLite 数据库…"
No modal and no layout changes.
```

## 03 同步完成

```text
Use the default screen as an exact layout and styling reference; change only completion state.
Central teal check ring: "同步完成", sublabel "268 个会话已一致", issue count "0 项待处理".
Both database diagnostics show healthy values: "缺失: 0  路径: 正常" and "Provider: custom".
Success rail: "同步完成：两份数据库与 JSONL 已一致".
Inline result summary: "JSONL 更新 184 · SQLite 新增 47 · 索引补齐 91".
Actions: "再次同步", "刷新状态", "打开备份目录".
Final log line: "[19:37:07] 同步完成，备份已保存".
Use teal/green only for verified success; no confetti or illustration.
```

## 04 SQLite 占用异常

```text
Use the default screen as an exact layout and styling reference; change only recoverable error state.
Central amber pause/lock ring: "SQLite 正在被占用", sublabel "未写入数据库更改".
Keep database rows visible and unchanged; issue count remains "632 项待处理".
Amber rail: "同步未完成：state_5.sqlite 正在使用中，请稍后重试".
Detail: "JSONL 与 session_index.jsonl 已从备份恢复".
Actions: "重试同步", "刷新状态", "打开备份目录".
Final log line: "[19:37:05] SQLite 被占用，已回滚并恢复文件".
No modal, no extra cards, no layout changes.
```
