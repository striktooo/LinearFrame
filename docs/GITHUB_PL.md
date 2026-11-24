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
2. zbuduje aplikację w wersji z tagu (np. 1.0.1) i dołoży do niej .NET (folder `runtime`),
3. spakuje `LinearFrame-1.0.1-win-x64.zip` (pełna paczka dla nowych osób), `LinearFrame-1.0.1-update.zip` (bez folderu
   `engine` — biorą ją zainstalowane kopie, w których mpv/FFmpeg się nie zmieniły) i `LinearFrame-1.0.1-app.zip` (same
   pliki aplikacji, kilka MB — biorą ją kopie od wersji 1.2.1, w których nie zmienił się też .NET), każdą z plikiem
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

Przy pierwszym uruchomieniu Windows pokaże niebieskie okno „System Windows ochronił ten komputer”, bo aplikacja nie ma
podpisu cyfrowego: **Więcej informacji → Uruchom mimo to** (obrazek: `docs/images/first-run-pl.png`, rysuje go
`scripts/first-run-picture.ps1`). Okno pojawia się raz — aktualizacje instalują się już bez niego. To nie jest wykrycie
wirusa: antywirus Defendera paczkę przepuszcza (sprawdzane przed każdym wydaniem na paczce oznaczonej jak pobrana z internetu).

**„Włącza się na chwilę i znika, ikonki nie ma”** — to nie SmartScreen, tylko Inteligentna kontrola aplikacji (włączona na
wielu nowych komputerach z Windows 11): wpuszcza `LinearFrame.exe`, blokuje niepodpisany `LinearFrame.dll` (kod 0x800711C7,
„Zasady kontroli aplikacji zablokowały ten plik”). Microsoft nie przewiduje wyjątku dla jednej aplikacji — właściciel
komputera może tylko wyłączyć tę ochronę w całości (Zabezpieczenia Windows → Kontrola aplikacji i przeglądarki) albo
poczekać na podpisaną wersję. Z 259 plików programu w paczce podpisu nie mają tylko cztery: `LinearFrame.exe`,
`LinearFrame.dll`, `engine\ffmpeg.exe`, `engine\libmpv-2.dll` (reszta to .NET i biblioteki Microsoftu).

Samo to okno nie zniknie „z czasem”: dla pliku bez podpisu Microsoft liczy uruchomienia od zera przy każdej wersji, a płatny
certyfikat też zaczyna od zera i potrzebuje setek instalacji. Okno zdejmują tylko dwie darmowe drogi:
[SignPath Foundation](https://signpath.org/terms) (darmowy podpis dla open source ich certyfikatem; wymaga zgłoszenia,
budowania wydań przez GitHub Actions i „weryfikowalnej reputacji” projektu) albo wydanie w Microsoft Store (paczka MSIX,
podpisuje Microsoft; darmowe konto dewelopera).

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
