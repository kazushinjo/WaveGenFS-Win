# WaveGenFS (Windows 版)

Windows 用の低周波信号発生器 (C# / .NET 8 WinForms)。全画面の波形表示に対応。Mac 版は [WaveGenFS-mac](https://github.com/kazushinjo/WaveGenFS-mac)。

## 入手
[Releases](https://github.com/kazushinjo/WaveGenFS-Win/releases) から `WaveGenFS-Win-<版>.zip` をダウンロードして展開し、`WaveGenFS.exe` を実行する。Windows 10/11 (64bit)。.NET のインストールは不要 (exe 単体で動く)。

署名していないため、初回は「Windows によって PC が保護されました」と出ることがある。「詳細情報」→「実行」で起動する。

## 主な機能
- 波形合成 (最大 5 系統、表示数は「Wave数」で 1〜5 を選ぶ): サイン / 矩形 / 三角 / ノコギリ / パルス / パルス(+-) / ホワイト・ピンク・M系列ノイズ
- 周波数 (Hz / 周期サンプル数), 振幅 (dB / 値 / %, 極性反転), オフセット, 位相, 出力 ch
- 詳細設定: 遅延, デューティ, 帯域制限 (PolyBLEP), ゲート, 周波数スイープ, AM/FM 変調
- クリップ防止 (合計ピークが 100% を超える振幅は設定不可)
- 出力減衰 1/N (ON/OFF), Volume最大
- FFT用に最適化 (Fs/N の整数倍), Wave2〜5 を Wave1 の倍数に設定 (周波数欄の右クリック)
- WAV 保存 (16/24bit, 32bit float), 設定ファイル (*.wgfs) 保存/読込 — Mac 版と同じ形式で相互に読める
- 全画面, 波形のみ表示, ホイールで時間軸 / Ctrl+ホイールで振幅軸
- L/R分離表示: 表示ch の右のボタンで L を上段・R を下段に分けて表示 (Stereo・Lch + Rch のとき)

## Wave の数
最大 5 系統 (Wave1〜5)。「Wave数」(表示ch の左) で 1〜5 個を選ぶ。隠した Wave は設定を残したまま出力しない (WAV・設定ファイルにも入れない)。設定ファイルを開いたとき、使っている Wave が隠れていれば表示数を増やす。設定ファイルは 8 系統の形式で保存し、Wave6〜8 は OFF。以前の版で Wave6〜8 を使っていたファイルは、読み込み時に OFF にしてお知らせを出す。

## キー操作
| 操作 | キー |
|---|---|
| 再生 / 停止 | F5 |
| 全画面 | F11 |
| 波形のみ | F12 |
| L/R分離表示 | Ctrl+D |
| 戻る (全画面・波形のみ解除) | Esc |
| 設定を開く / 保存 | Ctrl+O / Ctrl+S |
| WAV 保存 | Ctrl+E (Ctrl+W も可) |

設定は exe と同じフォルダの `WaveGenFS.json` に保存する。

## ビルド
```
dotnet build -c Release
```
配布用の exe 単体 (.NET 不要):
```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```
Mac / Linux でビルドするときは `-p:EnableWindowsTargeting=true` を付ける。アイコンは `app.ico` (Mac 版と同じサイン波)。

## ライセンス
GPL v3 (GNU General Public License version 3)。本文は [LICENSE](LICENSE)。
Copyright (C) 2026 Kazu Shinjo (JA6FUF/JH1XHX)

このプログラムはフリーソフトウェアです。GPL v3 (またはそれ以降の版) の条件で再配布・改変できます。無保証です。
