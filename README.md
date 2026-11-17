# Anotado

Editor de texto simples para Windows, feito com C# / WPF (.NET 10). Não depende de nenhuma biblioteca paga.

## Recursos

- Abrir e salvar `.rtf` (abre no Word) e `.txt`
- Fonte, tamanho, negrito, itálico, sublinhado, cor da fonte e realce
- Alinhamento, marcadores, numeração e recuo
- Inserir imagens e data/hora
- Localizar e substituir (Ctrl+F)
- Verificação ortográfica em pt-BR, com sugestões ao clicar com o botão direito
- Impressão (inclusive "Microsoft Print to PDF")
- Zoom (Ctrl + roda do mouse), contagem de palavras e aviso de alterações não salvas

## Como rodar

```
dotnet run
```

## Gerar o executável para distribuir

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

O executável fica em `bin/Release/net10.0-windows/win-x64/publish/Anotado.exe`.
