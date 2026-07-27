# ai-harness-deny

> Claude のツール実行を設定ファイルのルールで deny する ai-harness プラグイン。

`PreToolUse` で発火し、`config/ai-harness-deny.yml` の 4 系統（`rules` / `bash` / `powershell` / `files`）のいずれかにマッチしたツール実行をブロックする（`ExitCode=2`）。マッチしなければ許可。

## 設定（config/ai-harness-deny.yml）

```yaml
# settings.json 風の Tool("引数") ルール
rules:
  - Read("abc.yaml")
  - Bash("git commit")
  - PowerShell("git commit")

# Bash コマンドの部分一致 deny（含めばブロック）
bash:
  - "git show"

# PowerShell コマンドの部分一致 deny
powershell:
  - "Remove-Item"

# ファイルに触る全ツールを glob でブロック
files:
  - .claude/harness/*
```

## 4 系統の挙動

| 系統 | 対象 | マッチ方式 |
|---|---|---|
| `rules` | `Tool("引数")` で指定したツール | **シェル系**（Bash／PowerShell）… command の**前方一致**（`"git commit"` で始まる）。**ファイル系**（Read/Edit/Write 等）… file_path の **glob 一致**（`*`／`?`） |
| `bash` | Bash ツールの command | **部分一致**（指定文字列を**含む**コマンドを全て deny） |
| `powershell` | PowerShell ツールの command | `bash` と同じ**部分一致** |
| `files` | パスに触る全操作 | file_path の **glob 一致**（Read/Edit/Write 等）。加えて **Bash／PowerShell の command 内**にパスが現れた場合（`tail`/`cat`/`cp`、`Get-Content`/`Copy-Item` 等）も deny。相対パターンは絶対パスのサフィックスにもマッチ（`.claude/harness/*` が `/abs/.../.claude/harness/x` に効く） |

- `rules` の `Bash("git commit")` は前方一致のため、`git commit -m x` は deny、`sudo git commit` は許可。`PowerShell("…")` も同じ前方一致。
- `bash` の `"git show"` は部分一致のため、`git show`／`foo && git show HEAD` など含む全コマンドを deny。
- `bash` と `powershell` はツール別。両方を塞ぐには両方に書く。
- いずれか 1 つでもマッチすれば deny（先勝ち）。理由は client の stderr へ返る。

## ビルドと配置

```sh
dotnet build ai-harness-deny/ai-harness-deny/ai-harness-deny.csproj -c Release

cp ai-harness-deny/ai-harness-deny/bin/Release/net10.0/ai-harness-deny.dll  <配置先>/lib/
cp ai-harness-deny/config/ai-harness-deny.yml                                <配置先>/config/

<配置先>/ai-harness-main --restart   # 反映
```

`lib/` には `ai-harness-deny.dll` のみ置く（baselib.dll は host が共有ロード）。詳細は `ai-harness-main/docs/plugin-development.md` を参照。

## 構成

```
ai-harness-deny/
├── README.md
├── config/
│   └── ai-harness-deny.yml      ルール定義（配置元）
└── ai-harness-deny/
    ├── ai-harness-deny.csproj
    └── DenyPlugin.cs            PreToolUse で 3 系統を評価し deny
```
