# NewFolderThree

繁體中文介面的深層迷宮輔助外掛，衍生自 [transparent-duck/NewFolder3](https://github.com/transparent-duck/NewFolder3)。

## Dalamud 自訂外掛儲存庫

將以下庫鏈加入 Dalamud 的自訂外掛儲存庫，再於外掛安裝器搜尋 `NewFolderThree`。

```text
https://raw.githubusercontent.com/Angelways/NewFolderThree/refs/heads/main/NewFolderThree-release.json
```

## 原始碼建置

使用 `git clone --recurse-submodules https://github.com/Angelways/NewFolderThree.git` 取得原始碼與 OmenTools。使用 `global.json` 指定的 .NET SDK 和本機 Dalamud API 15 開發函式庫，執行 `dotnet build NewFolderThree.csproj -c Release -p:Platform=x64`。

[版本與下載](https://github.com/Angelways/NewFolderThree/releases)
