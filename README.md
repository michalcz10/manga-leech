# Manga Leech

Python aplikace pro stažení kapitol z celé stránky série WeebCentral nebo RSS/Atom feedu a vytvoření jednoho PDF pro každou kapitolu. Obrázky vybírá pouze ze sekce `#chapter-images`, v pořadí čtečky. Každý obrázek tvoří jednu stránku PDF s původním poměrem stran, bez ořezu; dlouhé webtoonové obrázky se nedělí na A4.

## Instalace (Windows / PowerShell)

Projekt počítá s Pythonem 3.15 dle zadání prostředí. Potřebujete instalovaný Python a virtuální prostředí:

```powershell
py -3.15 -m venv .venv
.\.venv\Scripts\python.exe -m pip install -r requirements.txt
```

Stahování používá standardní Python HTTP klient (`urllib`), podobně jako curl/wget. Prohlížeč ani Playwright nejsou potřeba. Přesměrování a cookies v rámci běhu se zpracují automaticky.

## Použití

```powershell
# Celá série od první kapitoly
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer"

# Ověření seznamu celé série bez stahování obrázků
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --list

# Všechny kapitoly ve feedu, jedno PDF na kapitolu
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss"

# Nejdříve pouze seznam
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --list

# Zkušebně první kapitola z feedu přes HTTP
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/rss" --limit 1 -o downloads
```

Pokud server vrací HTTP 403, odmítá daný požadavek. Aplikace nespouští JavaScript ani neřeší CAPTCHA nebo přihlášení. Vlastní identifikaci HTTP klienta lze nastavit pomocí `--user-agent "MujKlient/1.0"`.

URL série se rozpozná automaticky a aplikace načte úplný seznam odkazovaný tlačítkem „Show All Chapters“. U uvedené série bylo ověřeno 225 položek, seřazených od kapitoly 1. Pokud odkaz na úplný seznam chybí nebo je seznam prázdný, aplikace ohlásí chybu místo použití neúplného náhledu. `--limit 1` zde vybere kapitolu 1.

RSS je seznam posledních vydání, nikoli archiv celé série. Při ověření obsahoval uvedený feed 20 položek (kapitoly 204–223). Při zadání RSS aplikace zpracuje pouze obsah feedu; pro celý archiv zadejte URL série.

- Výchozí řazení je číselně od nejnižší kapitoly (1, 2, 2.5, 10), poté se aplikuje `--limit`. `--oldest-first` je ponecháno jako alias.
- `--newest-first`: řazení od nejvyšší kapitoly.
- `--chapter URL`: stáhne přímo jednu kapitolu místo RSS.
- `--force`: stáhne znovu obrázky i existující PDF.
- `--delay 1`: prodleva mezi požadavky, výchozí 0,5 s.
- `--timeout 60`: časový limit požadavku v sekundách.

PDF se ukládají do `downloads/`. Ověřené obrázky zůstávají v `downloads/.images/`, aby další spuštění mohlo navázat. Hotová PDF se přeskakují pouze tehdy, pokud souhlasí uložený otisk seznamu obrázků (`.pages.sha256`). Starší PDF bez tohoto záznamu se automaticky vytvoří znovu, včetně dříve neúplných jednostránkových PDF. Neúplná kapitola se jako PDF nezveřejní; chyba jedné kapitoly nezastaví ostatní. Návratový kód 0 znamená úspěch, 1 chybu, 130 přerušení. Poškozené nebo zastaralé hotové PDF lze obnovit pomocí `--force`.

## Testy

```powershell
.\.venv\Scripts\python.exe -m pip install pypdf
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Testy ověřují RSS/Atom, načtení úplného seznamu série místo náhledu, odstranění duplicit, chybu při chybějícím seznamu, načtení HTML fragmentu čtečky bez prohlížeče, pořadí a výběr obrázků, počet a rozměry PDF stran, navázání stahování a odmítnutí poškozených obrázků. Testy běží na dostupném Pythonu 3.12; Python 3.15 zde nebyl dostupný. Živě bylo ověřeno načtení všech 225 položek série a dříve také stažení všech 205 obrázků referenční kapitoly 1 do PDF s 205 stranami. Dostupnost serveru může kolísat; předchozí požadavky někdy vracely 403.

## Referenční kapitola 1

RSS obsahuje pouze kapitoly 204–223, proto po seřazení začne kapitolou 204. Kapitolu 1 lze stáhnout přímo:

```powershell
.\.venv\Scripts\python.exe manga_leech.py --chapter "https://weebcentral.com/chapters/01J76XZ3W1SDBG0SAGFG8RYXS4"
```

Opravený parser zpracovává i samouzavírací značky `<img />` a u referenční kapitoly načítá všech 205 obrázků.
