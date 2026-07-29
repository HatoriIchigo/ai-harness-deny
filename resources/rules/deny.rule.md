---
paths:
  - .claude/harness/config/ai-harness-deny.yml
---

## 概要

ai-harness-deny は `PreToolUse` で発火し、設定ファイル `ai-harness-deny.yml` の 4 系統
（`rules` / `bash` / `powershell` / `files`）のいずれかにマッチしたツール実行を deny する（deny 先勝ち）。

- `rules` … `Tool("引数")` 形式のルール。`Bash("…")`／`PowerShell("…")` は command の前方一致、ファイル系ツール（Read/Edit/Write 等）は file_path の glob 一致。
- `bash` … command の部分一致。指定文字列を含むコマンドをすべてブロックする。Bash 限定。
- `powershell` … `bash` と同じ部分一致。PowerShell 限定。
- `files` … file_path の glob 一致。file_path を引数に取るツールに加え、Bash／PowerShell の command 内にパスが現れた場合（tail/cat/cp、Get-Content/Copy-Item 等）もブロックする。相対パターンは絶対パスのサフィックスにもマッチする。

## 設定ファイル

`.claude/harness/config/ai-harness-deny.yml`

```yaml
# Tool("引数") 形式のルール。
#   Bash("...")／PowerShell("...") … command の前方一致
#   Read/Edit/Write 等             … file_path の glob 一致（* / ? 使用可）
rules:
  - Read("abc.yaml")
  - Bash("git commit")
  - PowerShell("git commit")

# 指定文字列を「含む」コマンドを部分一致でブロック（Bash 限定）。
bash:
  - "git show"

# 同上（PowerShell 限定）。
powershell:
  - "Remove-Item"

# ファイルに触る操作をブロック。glob（* / ?）使用可。相対パターンは絶対パスのサフィックスにもマッチ。
files:
  - .claude/harness/*
```
