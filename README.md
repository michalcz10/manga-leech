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
- `--workers 4`: počet současně stahovaných obrázků, výchozí 4. `--workers 1` stahuje postupně. Počet není vázaný na CPU, protože stahování čeká hlavně na síť.
- `--pdf-workers 1`: počet samostatných procesů pro tvorbu PDF, výchozí 1. PDF se generuje na pozadí souběžně se stahováním další kapitoly. Hodnota 2 umožní tvořit dvě PDF současně a vyžaduje více paměti.
- `--delay 1`: prodleva před každým požadavkem v jednotlivém vlákně, výchozí 0,5 s. Nejde o společný limit všech vláken; více vláken zvýší celkový počet požadavků za sekundu.
- `--timeout 60`: časový limit požadavku v sekundách.

Kapitoly se stahují postupně; uvnitř každé kapitoly se obrázky stahují paralelně. Po dokončení obrázků se kapitola předá samostatnému PDF procesu a ihned začne stahování další kapitoly. PDF zachovává původní pořadí čtečky. Fronta má nejvýše dvojnásobek `--pdf-workers` nedokončených PDF; při zaplnění stahování počká. Procesům se předávají pouze cesty k obrázkům. Před úspěšným ukončením aplikace počká na všechna PDF; chyba tvorby PDF vede k návratovému kódu 1. Při přerušení se zruší čekající úlohy a běžící práce doběhne, což může chvíli trvat. Ověřené obrázky zůstávají pro další spuštění.

```powershell
# Vlastní počet vláken
.\.venv\Scripts\python.exe manga_leech.py "https://weebcentral.com/series/01J76XYF6V9BDFSRRTY2C65SBS/The-Greatest-Estate-Developer" --workers 8 --pdf-workers 2
```

PDF se ukládají do `downloads/`. Ověřené obrázky zůstávají v `downloads/.images/`, aby další spuštění mohlo navázat. Hotová PDF se přeskakují pouze tehdy, pokud souhlasí uložený otisk seznamu obrázků (`.pages.sha256`). Starší PDF bez tohoto záznamu se automaticky vytvoří znovu, včetně dříve neúplných jednostránkových PDF. Neúplná kapitola se jako PDF nezveřejní; chyba jedné kapitoly nezastaví ostatní. Návratový kód 0 znamená úspěch, 1 chybu, 130 přerušení. Poškozené nebo zastaralé hotové PDF lze obnovit pomocí `--force`.

## Testy

```powershell
.\.venv\Scripts\python.exe -m pip install pypdf
.\.venv\Scripts\python.exe -m unittest discover -s tests -v
```

Testy ověřují RSS/Atom, načtení úplného seznamu série místo náhledu, odstranění duplicit, chybu při chybějícím seznamu, načtení HTML fragmentu čtečky bez prohlížeče, pořadí a výběr obrázků, počet a rozměry PDF stran, navázání stahování a odmítnutí poškozených obrázků. Dále ověřují skutečný souběh stahování, zachování pořadí v PDF při opačném pořadí dokončení a samostatné HTTP klienty vláken. Testy také ověřují překryv tvorby PDF se stahováním další kapitoly, dokončení PDF před návratem, hlášení chyb tvorby PDF a skutečné spuštění PDF procesu. Testy navíc porovnávají vložené JPEG streamy s původními soubory (RGB, odstíny šedi i CMYK). Všech 14 testů prošlo na dostupném Pythonu 3.12; Python 3.15 zde nebyl dostupný. Živě bylo dříve ověřeno načtení všech 225 položek série a také stažení všech 205 obrázků referenční kapitoly 1 do PDF s 205 stranami. Dostupnost serveru může kolísat; předchozí požadavky někdy vracely 403.

## Referenční kapitola 1

RSS obsahuje pouze kapitoly 204–223, proto po seřazení začne kapitolou 204. Kapitolu 1 lze stáhnout přímo:

```powershell
.\.venv\Scripts\python.exe manga_leech.py --chapter "https://weebcentral.com/chapters/01J76XZ3W1SDBG0SAGFG8RYXS4"
```

Opravený parser zpracovává i samouzavírací značky `<img />` a u referenční kapitoly načítá všech 205 obrázků.

## Velikost PDF

JPEG obrázky se vkládají v původní komprimované podobě bez překódování a bez ztráty kvality. Formát se rozpoznává podle obsahu i u cache souborů s příponou `.img`. Ostatní formáty se ukládají bezeztrátově pomocí Flate; u nich není zaručená velikost blízká původním souborům. Rozměry obrázků se nemění. Binární obrazové streamy se zbytečně nekódují přes ASCII85.

Ověřeno na kapitole 1 s 205 JPEG obrázky: zdroj 13,43 MB, původní PDF 139,55 MB, nové PDF 13,57 MB. Nové PDF vzniklo přibližně za 1,6 sekundy na testovacím stroji. Všech 205 vložených JPEG streamů odpovídá původním souborům bajt po bajtu.

Verze způsobu vytváření PDF je součástí otisku dokončené kapitoly. Starší PDF se při běžném spuštění automaticky přestaví z ověřené cache, bez nutnosti `--force` a opětovného stahování existujících obrázků. Seznam kapitol a obrázků se nadále načítá ze serveru.
