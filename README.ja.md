<div align="center">

<img src="./Resources/Brand/Ta-AppIcon.png" width="128" alt="Ta ロゴ">

# 拓 · Ta

### 千年前、紙と墨は石の文字を拓き取った。今、AI が画面の情報を拓き取る。

[![License: MIT](https://img.shields.io/badge/License-MIT-D6402F.svg)](./LICENSE)
[![Platform: macOS 14+](https://img.shields.io/badge/macOS-14%2B-1A1A1A.svg)](https://www.apple.com/macos/)
[![Platform: Windows 10 2004+](https://img.shields.io/badge/Windows-10%202004%2B-1A1A1A.svg)](./windows)
[![Swift: 6.2](https://img.shields.io/badge/Swift-6.2-F05138.svg)](https://www.swift.org/)
[![Release: v1.0.1](https://img.shields.io/badge/Release-v1.0.1-C98B2E.svg)](https://github.com/kangarooking/Ta/releases/tag/v1.0.1)

**キャプチャ、OCR、AI 画像理解、翻訳、スクロールキャプチャ、ピン留め、注釈を一つの流れにまとめた AI ネイティブ・スクリーンショットツールです。**

macOS 14+ 版を公開中です。**Windows プレビュー版**（.NET 8 への移植、ソースは [`windows/`](./windows)）もダウンロードできます。

[简体中文](./README.md) · [English](./README.en.md) · [日本語](./README.ja.md)

</div>

![Ta ホーム画面](./docs/brand/Ta-home-preview.png)

## 千年以上前、中国には「スクリーンショット」の原型があった

カメラもコピー機も現代の印刷技術もなかった時代、人々には現実的な課題がありました。石碑に刻まれた文字を、どうすれば持ち帰れるのか。

人々は石碑に紙をかぶせ、墨を含ませた道具でそっと叩きました。紙を剥がすと、文字は石を離れて人とともに家へ帰ります。この技法が、千年以上受け継がれてきた**拓印**です。見たものを写し取り、手元に残す、古代のスクリーンショットとも言えます。

「拓」という字も、その行為を表しています。`扌（手）+ 石`、石に手を当てる姿です。中国語では `tà` と読み、英語名は **Ta** です。

現代では、石碑が画面に変わりました。情報は増え、消える速度も速くなっています。Ta がすることは同じです。範囲を囲み、拓き取る。文字はコピーや翻訳ができ、画像は注釈を付けたり画面に留めたりできます。スクロールキャプチャは碑全体を端から端まで拓き取ること。AI は、内容を読み解く携帯の金石学者です。

> 倉頡が文字を生み、拓印が文字を伝えた。情報は生まれただけでは終わらない。残され、理解され、持ち運べてこそ完成する。

## 私が Ta を作った理由

スクリーンショットツールは、私がほぼ毎日使うソフトウェアです。無料・有料を問わず多くの製品を試しましたが、必要なことを一つですべて満たすものは見つかりませんでした。OCR、翻訳、スクロールキャプチャ、画像ピン、注釈、画像の美化が、それぞれ別のアプリや操作に分かれていたからです。

そこで、自分で作ることにしました。

多くのスクリーンショットアプリは、従来の流れに OCR や単独の AI ボタンを追加しています。Ta は AI を操作の流れそのものに組み込み、まず私自身の、そして皆さんの毎日の実用的なニーズに沿って改善し続ける試みです。

## 「AI ネイティブ・スクリーンショットツール」とは

AI ネイティブとは、従来のスクリーンショットアプリにチャット欄を付けることではありません。キャプチャした瞬間から AI が作業を支えることです。

- **拓き取る** — 範囲、ウインドウ、スクロールページをすばやくキャプチャ。
- **読み取る** — ローカル OCR またはマルチモーダルモデルで文字、表、数式、コードを抽出。
- **理解する** — 画面上の情報を翻訳、説明、構造化。
- **活用する** — コピー、ピン留め、注釈、美化、保存、追加処理へ接続。
- **境界を守る** — ローカル認識を優先し、クラウド処理前に確認し、API Key は macOS Keychain に保存。

## 解決する課題

- **撮影後の二次作業が多い** — OCR、翻訳、コピー、保存、注釈を一つのフローにまとめます。
- **スクロールキャプチャが不安定** — 手動/自動スクロール、重複フレーム除去、固定領域除去、継ぎ目確認、手動補正に対応します。
- **一つの OCR では足りない** — 速度、構造、プライバシーに応じて Apple Vision、PaddleOCR、リモート視覚サービスを切り替えられます。
- **注釈に時間がかかる** — Snipaste に近いその場での注釈、オブジェクトの直接操作、ブラシ型モザイク、画像ピンを提供します。
- **クラウド送信の境界が曖昧** — デフォルトはローカル処理。送信時は明示し、API Key は macOS Keychain に保存します。

## 定番の使い方

以下はすべて、現在動作している Ta から取得したスクリーンショットです。

<table>
  <tr>
    <td width="50%" valign="top">
      <strong>一度囲んで、次の操作を選ぶ</strong><br><br>
      <img src="./docs/showcase/02-capture-toolbar.png" alt="Ta 共通キャプチャツールバー">
      <br>文字抽出、AI 画像認識、翻訳、コピー、ピン留め、注釈、美化、保存へそのまま進めます。
    </td>
    <td width="50%" valign="top">
      <strong>自分の AI モデルを設定</strong><br><br>
      <img src="./docs/showcase/05-ai-model-settings.png" alt="Ta AI モデル設定">
      <br>複数の Provider プロトコルに対応し、API Key は macOS Keychain に保存します。
    </td>
  </tr>
  <tr>
    <td width="50%" valign="top">
      <strong>その場で注釈</strong><br><br>
      <img src="./docs/showcase/03-annotation-tools.png" alt="Ta その場で使える注釈ツールバー">
      <br>矢印、文字、ハイライト、二種類のモザイク、オブジェクトの直接拡大縮小に対応します。
    </td>
    <td width="50%" valign="top">
      <strong>参考画像を画面に固定</strong><br><br>
      <img src="./docs/showcase/04-pin-image.png" alt="Ta 画像ピンの例">
      <br>画像ピンは常に手前に表示され、移動、拡大縮小、透明度変更、ダブルクリックでの終了ができます。
    </td>
  </tr>
</table>

## 仕組み

Ta は macOS ネイティブのキャプチャパイプラインを使用します。

```text
グローバルショートカット
    ↓
画面領域を選択
    ↓
ScreenCaptureKit で撮影（Ta 自身のウインドウは除外）
    ↓
┌────────────────┬────────────────────┐
│ ローカル OCR    │ マルチモーダル視覚   │
│ Apple Vision   │ OpenAI-compatible  │
│ PaddleOCR      │ Claude / Gemini    │
└────────────────┴────────────────────┘
    ↓
コピー · 翻訳 · ピン留め · 注釈 · 結合 · 保存
```

認識中にユーザーが別の内容をコピーした場合、Ta は新しいクリップボード内容を上書きしません。文字が見つからない場合は、PNG のコピーへ安全にフォールバックできます。

## 主な機能

### キャプチャとショートカット

- OCR、AI 画像認識、翻訳、コピー、ピン留め、注釈、美化、保存を選べる共通ツールバー。
- 即時 OCR、画像コピー、翻訳、ピン留め、スクロールキャプチャに専用グローバルショートカット。
- 設定画面でショートカットを再記録し、競合を検出して初期値へ戻せます。
- 領域選択中は右クリックまたは `Escape` で中止でき、ファイルやクリップボードを変更しません。
- 撮影時は Ta の画面を自動的に隠し、フォーカスを奪わず、キャプチャにも映り込みません。

### OCR と AI 画像認識

- **Apple Vision** — 中国語、英語、一般的な文字レイアウトに対応する標準のローカル OCR。
- **PaddleOCR 拡張パック** — Apple Silicon 上でオフライン動作し、インストール、更新、検証、ウォームアップ、常駐ワーカーに対応。
- **DeepSeek-OCR-2** — ユーザーが用意した vLLM、SGLang、または互換視覚エンドポイントへ接続。大型モデルを Mac へ無断でダウンロードしません。
- **マルチモーダル Provider** — OpenAI-compatible、Azure OpenAI、Anthropic Claude、Google Gemini の各プロトコル。
- 正確な文字抽出、コード説明、Markdown/CSV 表、LaTeX 数式、一般画像理解のタスクテンプレート。
- OCR のみ、モデルのみ、またはアップロード前に確認するローカル優先スマートルーティング。

### スクリーンショット翻訳

- 翻訳元と翻訳先の言語をカスタマイズ可能。初期値は自動判定から簡体字中国語です。
- キャプチャを翻訳し、結果をクリップボードへ直接コピーします。
- プレーンテキスト、画像内の文字置換、原画像下部への二言語パネル追加に対応します。
- 文字位置の検出と最終画像の合成はローカルで行い、翻訳が必要な文字だけを設定済みモデルへ送信します。

### AI 美化（開発中）

- SNS 投稿や製品ドキュメント向けに、余白、角丸、影、背景、定番レイアウトを自動で整えます。
- スマートな注釈、個人情報のマスキング、複数サイズ書き出し、内容に合う背景や装飾要素の生成を計画しています。
- 現在のバージョンには「美化」の入口を用意していますが、完全な AI 美化ワークフローは開発中であり、完成済み機能としては扱っていません。

### スクロールキャプチャ

- ブラウザ、チャット、一般的なデスクトップアプリで手動/自動スクロール撮影。
- 隣接フレームの照合、重複除去、スクロール方向の検出。
- 固定ヘッダー、フッター、入力欄の検出と除去。
- 書き出し前に継ぎ目を確認し、`±1` / `±10 px` 単位で補正できます。
- 極端に長い画像を分割し、メモリ使用量と書き出し負荷を抑えます。

### 注釈と画像ピン

- キャプチャ位置を保ったまま、半透明オーバーレイ上で直接注釈できます。
- 四角形、楕円、矢印、ペン、蛍光ペン、文字、番号、モザイク、ぼかし、消しゴム、拡大鏡。
- 注釈オブジェクトを直接選択し、移動、拡大縮小、回転、再編集できます。
- モザイクは矩形選択とフリーハンド描画の両方に対応します。
- 画像ピンは移動、サイズ、透明度、回転、反転、フィルター、切り抜き、クリック透過、グループ、非表示/復元、ダブルクリックで閉じる操作に対応します。
- スクリーンショット、クリップボード画像、文字、HTML、ファイルからピンを作成できます。

## クイックスタート

### 必要環境

- macOS 14 以降
- Apple Silicon または Intel Mac（配布用 PaddleOCR 拡張パックは現在 Apple Silicon 向け）
- Xcode 26、または Swift 6.2 互換ツールチェーン

### ダウンロード

[**Ta v1.0.1 をダウンロード（macOS Universal DMG）**](https://github.com/kangarooking/Ta/releases/latest/download/Ta-1.0.1-macOS-universal.dmg)

Apple Silicon と Intel Mac の両方に対応しています。DMG を開き、「拓」を `Applications` へドラッグしてください。現在のビルドは Apple notarization 未完了のため、初回起動時は Finder で Control キーを押しながらアプリをクリックし、「開く」を選んでもう一度確認してください。

[**Ta Windows プレビュー版をダウンロード（ZIP・インストール不要）**](https://github.com/qbdx-hub/Ta/releases/download/windows-v0.1.0/Ta-windows-v0.1.0-x64.zip)

Windows 10 version 2004 以降 / Windows 11 に対応しています。**.NET ランタイム同梱・インストール不要**：解凍後、まず `Ta.Shell.exe`（常駐トレイとグローバルショートカット）を起動し、次に `Ta.Settings.exe`（パネルと設定）を起動してください。[`windows/`](./windows) からビルドした Windows 移植プレビュー版で、コード署名は未実施のため、初回起動時に SmartScreen の警告が表示される場合があります。その場合は「実行」を選んでください。

### ソースからビルド

```bash
git clone https://github.com/kangarooking/Ta.git
cd Ta
swift test
./scripts/build-app.sh
open "artifacts/拓.app"
```

初回起動時に「画面収録とシステムオーディオ録音」の権限を許可してください。「アクセシビリティ」権限は自動スクロールキャプチャを使う場合だけ必要です。

### デフォルトショートカット

| 操作 | ショートカット |
|------|----------------|
| 即時 OCR | `⇧⌥⌘1` |
| 共通キャプチャ | `⇧⌥⌘2` |
| 画像をコピー | `⇧⌥⌘3` |
| キャプチャしてピン留め | `⇧⌥⌘4` |
| スクロールキャプチャ | `⇧⌥⌘5` |
| スクリーンショット翻訳 | `⇧⌥⌘6` |

「設定 → ショートカット」で任意の組み合わせを再登録できます。競合するショートカットは拒否されるか、自動的に元へ戻ります。

## Agent から Ta を使う

Ta は Agent の視覚入力レイヤーとして、次の三つの形で利用できます。

- **Ta Agent Skill** — 対応 Agent に、キャプチャ、OCR、画像理解、翻訳を安全に組み合わせる方法を伝えます。
- **`ta` CLI** — Shell、スクリプト、一般的な Agent 向けに安定した JSON コマンドを提供します。
- **`dsh-ta`** — DeepSeek Harness にツールを直接登録するネイティブ Cordis Plugin + Bundle です。

三つとも「拓.app」がホストするローカル Bridge を利用します。通常の Agent キャプチャでは Ta を表示せず、フォーカスを奪わず、ポインターを移動せず、キーイベントも送信しません。権限、モデル設定、API Key は引き続き Ta が管理します。

次の一行で `ta` CLI と Ta Agent Skill をまとめてインストールまたは更新できます。

```bash
curl -fsSL --retry 3 --retry-all-errors --retry-delay 1 https://github.com/kangarooking/Ta/releases/latest/download/install.sh | bash
```

インストーラーは SHA-256 を検証し、CLI を `~/.local/bin/ta` に、Skill を Codex と一般的な Agent Skills ディレクトリに配置します。Agent を再起動してから接続を確認してください。

```bash
ta status --json
ta capture frontmost --json
ta ocr last --json
```

「設定 → Agent」では、自動化の無効化、クラウド処理の禁止、Bundle ID による機密 App のブロック、キャッシュ削除、内容を伏せた最近の呼び出し履歴を確認できます。CLI、Skill、DeepSeek Harness の導入手順は [Agent 統合ガイド](./docs/agent-integration.md) を参照してください。

## プライバシーとセキュリティ

- 通常のキャプチャと Apple Vision OCR は常に端末内で処理します。
- PaddleOCR 拡張パックはインストール後、オフラインで動作します。
- リモート OCR、マルチモーダル画像認識、翻訳を明示的に選んだ場合だけ、選択領域または文字を設定済みサービスへ送信します。
- 低信頼度スマートルーティングは無断でアップロードせず、必ず確認を求めます。
- API Key は macOS Keychain のみに保存し、設定ファイル、ログ、リポジトリには書き込みません。
- Ta は画面を常時録画せず、ユーザーが選択した領域だけを読み取ります。
- Agent Bridge は現在のユーザーだけが使えるローカル Unix Socket を利用し、監査ログにはリクエスト引数、認識本文、画像データを保存しません。

## リポジトリ構成

```text
Ta/
├── README.md / README.en.md / README.ja.md
├── Package.swift
├── Resources/                 アイコン、Info.plist、ブランド素材
├── Sources/
│   ├── AIScreenshotCore/      OCR、長画像結合、Provider、Clipboard
│   ├── AIScreenshotApp/       撮影、エディタ、ルーティング、システム、UI
│   ├── TaAgentContracts/      Bridge プロトコル
│   ├── TaAgentClient/         ローカル Bridge クライアント
│   └── TaCLI/                 ta CLI
├── Integrations/              Agent Skill と DeepSeek Harness ネイティブプラグイン
├── Tests/                     Core / App テスト
├── ocr-packs/paddleocr/       PaddleOCR 拡張パック定義
├── scripts/                   ビルド、実行、OCR パックスクリプト
└── docs/                      PRD、調査、検証記録、実装計画
```

## 現在のステータス

Ta v1.0.1 は現在の公開版です。Apple Silicon と Intel Mac の両方に対応する Universal DMG と ZIP を提供します。現在のビルドは Apple notarization 未完了のため、初回起動時は Finder で Control キーを押しながらアプリをクリックし、「開く」を選ぶ必要があります。

既知の制限：

- 領域選択は現在ポインターがあるディスプレイを中心に動作し、複数画面をまたぐ選択とウインドウ自動スナップは未完成です。
- 動画、アニメーション、半透明オーバーレイ、大きく再配置されるレイアウトでは、スクロールキャプチャの継ぎ目を手動補正する場合があります。
- 画像翻訳はローカルで文字を覆って再描画します。複雑なテクスチャ、グラデーション、影、縦書き、密なレイアウトでは跡が残ることがあります。
- 公開リポジトリには PaddleOCR パックの定義のみを含み、ローカルで生成した大型アーカイブやモデル重みは含めません。
- v1.0.1 は Apple Development 署名を使用しています。Developer ID 署名と Apple notarization は引き続き対応中です。

## ドキュメント

- [製品要件（中国語）](./AI截图软件-产品需求文档-PRD-v1.0.md)
- [市場・ユーザー課題調査（中国語）](./AI截图软件市场与用户痛点调研.md)
- [Alpha 検証記録](./docs/alpha-verification.md)
- [スクロールキャプチャ受け入れ基準](./docs/long-capture-acceptance-matrix.md)
- [PaddleOCR 拡張パック仕様](./docs/ocr-enhancement-pack-spec.md)
- [Agent Skill、CLI、DeepSeek Harness 統合ガイド](./docs/agent-integration.md)

## 展望：スクリーンショットを Agent の目に

Ta は、機能の多いスクリーンショットツールだけを目指しているわけではありません。将来はより多くの **Agent 機能**を取り入れ、静止画像を、画面の理解と作業の実行につながる入口へ変えていきます。

ユーザーが明示的に確認したうえで、Agent は次のようなことを行えるようになります。

- タスク、日付、リンク、表を認識し、メモ、Todo、構造化データへ整理する。
- 画面の状態を理解して次の操作を提案し、翻訳、注釈、美化、書き出しを一つの流れにつなぐ。
- 電話番号、メールアドレス、アバターなどの機密情報を検出して隠す。
- 長い会話、コードエラー、製品ページ、調査資料を編集可能な成果物へ変換する。
- よく使う処理を学び、反復操作を再利用できる個人ワークフローにする。

Agent 機能も、明確な許可、見える処理、取り消せる結果を原則にします。Ta は画面を勝手に操作するのではなく、画面上の情報を持ち出し、理解し、次の仕事へつなげるための道具です。

## Roadmap

- [x] ネイティブキャプチャ、OCR、クリップボード出力、カスタムショートカット
- [x] スクロールキャプチャ、自動スクロール、継ぎ目確認
- [x] その場での注釈、ブラシ型モザイク、画像ピン
- [x] スクリーンショット翻訳と複数 Provider 設定
- [x] オフライン PaddleOCR 拡張パックの仕組み
- [x] Agent Skill、`ta` CLI、DeepSeek Harness ネイティブプラグイン
- [ ] 複数画面をまたぐ領域選択とウインドウスナップ
- [ ] 公開用テンプレートとパラメータ化された画像スタイル
- [ ] AI 美化、スマートな個人情報マスキング、複数サイズ生成
- [ ] 履歴、検索、結果の再コピー
- [ ] 確認可能で取り消せるスクリーンショット Agent ワークフロー
- [x] Windows 版（.NET 8 移植、[`windows/`](./windows)）
- [x] macOS Universal DMG、ZIP、チェックサム
- [ ] Developer ID 署名と Apple notarization

## 無料・オープンソース、そして一緒に育てる

Ta は [MIT License](./LICENSE) のもとで無料公開されています。誰でもソースコードをダウンロードし、ビルド、利用、変更、再配布できます。

Ta がまだ解決できていないスクリーンショットの課題があれば、Issue で教えてください。改善に参加していただける Pull Request も歓迎します。変更前に [CONTRIBUTING.md](./CONTRIBUTING.md) を読み、挙動の変更にはテストまたは再現可能な検証手順を添えてください。

Ta がアプリの切り替えや反復作業を一つでも減らせたなら、ぜひ **Star** をお願いします。それが開発への最も直接的な応援になります。

## 作者について

**袋鼠帝 kangarooking** — AI ブロガー、インディー開発者。AI Top 公式アカウント「袋鼠帝 AI 客栈」主宰

Volcengine ナビゲーション KOL、Baidu Qianfan 開発者アンバサダー、GLM エバンジェリスト、Trae 昆明初代 Fellow

| プラットフォーム | リンク |
|------------------|--------|
| 𝕏 Twitter | https://x.com/aikangarooking |
| 小紅書 | https://xhslink.com/m/5YejKvIDBbL |
| 抖音 | https://v.douyin.com/hYpsjphuuKc |
| WeChat 公式アカウント | 袋鼠帝 AI 客栈 |
| WeChat ビデオチャンネル | AI 袋鼠帝 |

WeChat 公式アカウント「袋鼠帝 AI 客栈」QR コード:

![](https://raw.githubusercontent.com/kangarooking/cangjie-skill/main/assets/kangarooking-gzh.png)

Ta の使い方を共有したり、スクリーンショットの課題を報告したり、AI ネイティブなスクリーンショットツールの開発に参加したい方は、Ta の WeCom コミュニティにご参加ください：

<img src="https://raw.githubusercontent.com/kangarooking/Ta/main/assets/wecom-ta-group-qr.png" width="220" alt="Ta WeCom コミュニティ QR コード">

## ⭐ Star History

Ta が役に立ったら、ぜひ Star をお願いします。

<a href="https://www.star-history.com/?repos=kangarooking%2FTa&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
   <img alt="Ta Star History Chart" src="https://api.star-history.com/chart?repos=kangarooking/Ta&type=date&legend=top-left" />
 </picture>
</a>

## License

MIT。詳細は [LICENSE](./LICENSE) を参照してください。
