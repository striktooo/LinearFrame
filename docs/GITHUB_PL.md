# LinearFrame na GitHubie — ściąga dla autora

Repozytorium: **https://github.com/striktooo/LinearFrame** · wydania: **https://github.com/striktooo/LinearFrame/releases**

## Jak wypuścić nową wersję (poprawki, nowe funkcje)

Wszyscy, którzy mają LinearFrame, dostaną ją **automatycznie** (aplikacja sprawdza wydania dwa razy dziennie, pobiera ZIP,
sprawdza sumę SHA-256 i podmienia się po cichu, gdy nikt z niej nie korzysta). Nie potrzebujesz żadnego serwera.

```powershell
# 1. zmiany w kodzie... potem zapisz je:
git add .
git commit -m "Opis zmian"
git push

# 2. nadaj numer wersji (zawsze wyższy niż poprzedni!) i wypchnij tag:
git tag v1.0.1
git push origin v1.0.1
```

GitHub Actions (plik `.github/workflows/release.yml`) sam:
1. pobierze libmpv i FFmpeg,
2. zbuduje `LinearFrame.exe` w wersji z tagu (np. 1.0.1),
3. spakuje `LinearFrame-1.0.1-win-x64.zip` (pełna paczka dla nowych osób) i `LinearFrame-1.0.1-update.zip` (mała paczka
   bez folderu `engine` — zainstalowane kopie pobierają ją zamiast pełnej, gdy mpv/FFmpeg się nie zmieniły), obie z plikiem
   `.sha256`, i opublikuje je w **Releases**.

Postęp zobaczysz w zakładce **Actions** (ok. 5–10 minut). Numer wersji w `LinearFrame.csproj` nie musi być ręcznie zmieniany —
workflow bierze go z tagu.

> Numeracja: `v1.0.1` — poprawka błędu, `v1.1.0` — nowa funkcja, `v2.0.0` — duża zmiana.
> Aktualizator instaluje tylko wersję **wyższą** niż zainstalowana.

### Ręcznie, bez Actions

```powershell
pwsh .\build.ps1 -UseLocalFfmpeg
gh release create v1.0.1 (Get-ChildItem dist\LinearFrame-1.0.1-*).FullName --title "LinearFrame 1.0.1" --notes "Co nowego…"
```
(najpierw podnieś `<Version>` w `src/LinearFrame/LinearFrame.csproj`, żeby exe znało swój numer). Do wydania trafiają cztery
pliki: pełny ZIP, mały ZIP aktualizacyjny i ich pliki `.sha256` — aktualizator sprawdza nimi, czy pobrana paczka nie została
uszkodzona i czy zainstalowany silnik wideo nadal pasuje.

## Jak kolega pobiera

Link: **https://github.com/striktooo/LinearFrame/releases/latest** → `LinearFrame-…-win-x64.zip` → rozpakować → `LinearFrame.exe`.

Przy pierwszym uruchomieniu Windows SmartScreen może napisać „System Windows ochronił ten komputer”, bo aplikacja nie ma
certyfikatu podpisu kodu: **Więcej informacji → Uruchom mimo to**. Ostrzeżenie słabnie z czasem, gdy więcej osób pobiera plik.
Darmowy podpis dla projektów open source oferuje [SignPath Foundation](https://signpath.org) — opcja na później.

## Przydatne komendy

| Co | Komenda |
|---|---|
| Zobaczyć zmiany | `git status` |
| Zapisać zmiany | `git add .` → `git commit -m "opis"` |
| Wysłać na GitHuba | `git push` |
| Nowe wydanie | `git tag v1.0.1` → `git push origin v1.0.1` |
| Lista wydań | `gh release list` |
| Postęp budowania | `gh run list` / `gh run watch` |
| Repo w przeglądarce | `gh repo view --web` |

## Dobre praktyki

- **Zgłoszenia błędów:** zakładka *Issues* — ludzie mogą tam pisać, co nie działa.
- **Zrzuty w README** są w `docs/images/`. Generuje je sama aplikacja (`LinearFrame.exe --marketing <folder>`), więc po zmianach
  w wyglądzie łatwo je odświeżyć.
- **Licencja:** MIT dla Twojego kodu. Paczka do pobrania zawiera mpv i FFmpeg na licencji GPL — opisane w `THIRD-PARTY-NOTICES.md`.
