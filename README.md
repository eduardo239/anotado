# Anotado

Editor de texto para Windows, feito com C# / WPF (.NET 10). Não depende de nenhuma biblioteca paga.

## Recursos

- Abrir e salvar `.rtf` (abre no Word) e `.txt`
- Abas Início, Inserir, Layout, Revisão e Exibir
- Estilos de parágrafo: Texto normal, Título, Título 1, Título 2 e Citação
- Fonte, tamanho, negrito, itálico, sublinhado, cor, realce, alinhamento, listas e espaçamento entre linhas
- Inserir imagem, tabela, link (Ctrl+K) e data/hora
- Painel de navegação com busca e estrutura do documento (gerada a partir dos títulos)
- Painel do documento: formato (A4, Carta, Ofício), orientação, margens, estatísticas e meta de palavras
- Modo foco (F11), modo leitura e temas claro, escuro ou automático
- Verificação ortográfica em pt-BR, localizar e substituir, impressão (inclusive em PDF) e zoom

As preferências ficam em `%APPDATA%\Anotado\config.json`.

## Estrutura

| Arquivo | Conteúdo |
|---|---|
| `MainWindow.xaml` / `.cs` | Janela principal e lógica do editor |
| `Themes/Styles.xaml` | Estilos visuais (botões, abas, cartões) |
| `Themes/Light.xaml`, `Themes/Dark.xaml` | Cores de cada tema |
| `ThemeManager.cs` | Troca de tema |
| `AppSettings.cs` | Preferências e tamanhos de página |
| `Dialogs.cs` | Janelas de link e de atalhos |

## Como rodar

```
dotnet run
```

## Gerar o executável para distribuir

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
