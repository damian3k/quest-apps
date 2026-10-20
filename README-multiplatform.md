# Quest Apps 4.3

**[English](#english) · [Polski](#polski)**

---

<a id="english"></a>
## English

Quest Apps is a desktop tool (Windows, macOS, Linux) for Meta Quest headsets (tested on Quest 3). It talks to the headset over ADB (by USB cable or Wi-Fi) and lets you:

- see every installed app with its size, version and icon, and how full the storage is,
- uninstall apps and delete large leftover files,
- install APKs (with OBB game data) by drag & drop,
- launch and stop apps from the PC,
- **lock** an app (kiosk mode) so it cannot be left,
- control **many headsets at once**: install APKs, and launch, lock and unlock the same app on all of them.

### 1. Requirements

- A computer with one of:
  - **Windows 10/11 x64.** Nothing else to install: `adb` ships in the `adb\` folder next to `QuestApps.exe`. Keep the two together.
  - **macOS 12 or newer**, Apple Silicon or Intel. adb has to be installed, see below.
  - **Linux x64 or ARM64.** adb has to be installed, see below.
- A Quest headset with **Developer Mode** turned on.
- A USB-C cable that carries data (not a charge-only cable), needed once per headset.
- For Wi-Fi: the computer and the headset on the **same network**. Guest networks and networks that isolate devices from each other block it.

#### Installing on macOS

1. Unzip the right file:
   - `QuestApps-4.3-macos-arm64.zip` for Apple Silicon (M1–M4),
   - `QuestApps-4.3-macos-x64.zip` for Intel Macs.
2. Move `QuestApps.app` to **Applications**.
3. Install adb with Homebrew: `brew install --cask android-platform-tools`. Android Studio's adb works too.
4. The app is not signed by an Apple developer, so before the **first** start run this once in Terminal. The second line is required on Apple Silicon; without it the app is closed straight away.
   ```bash
   xattr -dr com.apple.quarantine /Applications/QuestApps.app
   codesign --force --deep -s - /Applications/QuestApps.app
   ```
5. Start Quest Apps normally. No USB driver is needed on a Mac.

#### Installing on Linux

1. Unpack: `tar xzf QuestApps-4.3-linux-x64.tar.gz` (the `-linux-arm64` file is for ARM boards).
2. Install adb:
   - Debian/Ubuntu: `sudo apt install adb`
   - Fedora: `sudo dnf install android-tools`
   - Arch: `sudo pacman -S android-tools`
3. Allow adb to reach the headset over USB, then unplug and plug the cable back in. Without this, the headset shows up as *no permissions*.
   ```bash
   sudo cp 99-meta-quest.rules /etc/udev/rules.d/ && sudo udevadm control --reload-rules
   ```
4. Start the app with `./QuestApps`.
5. *(Optional)* Menu entry: copy `questapps.desktop` to `~/.local/share/applications/` and `questapps.png` to `~/.local/share/icons/`. In the `.desktop` file, set `Exec=` to the full path of `QuestApps`.

On macOS and Linux, wherever this guide says `QuestApps.exe`, use Quest Apps / `QuestApps`.

### 2. First-time setup of a new headset (pairing)

Every new headset must be connected **by cable once**. That is when the headset learns to trust your PC. Wi-Fi cannot be set up for a headset that has never been connected by cable.

1. **Turn on Developer Mode.** Open the Meta Horizon app on your phone → Devices → pick the headset → Headset settings → Developer Mode → on. Restart the headset. The exact menu names can differ between app versions. Meta may also ask you to create or join a developer organization on its website first.
2. **Close SideQuest and Meta Quest Developer Hub** if they are open. Each one brings its own adb, and two adb versions running at the same time keep disconnecting each other.
3. Start `QuestApps.exe` and **connect the headset by USB**. Keep the headset awake.
4. **Put the headset on.** A prompt says *"Allow USB debugging?"*. Tick **"Always allow from this computer"** and press **Allow**.
   - Until you do this, the headset list shows it as *unauthorized*.
   - With "Always allow" ticked, this prompt never comes back for this PC.
5. Press **Refresh**. The headset shows up as *"Quest 3 •0402 — USB"* and its apps load.
6. **Turn on Wi-Fi.** Press **Enable Wi-Fi** in the toolbar. After a few seconds the list shows *"USB + Wi-Fi"* and the status says you can unplug the cable.
7. *(Recommended)* Tick **Keep awake**. A headset taken off the head normally falls asleep after a short while, and the Wi-Fi connection drops with it. Keep awake prevents that, at the cost of more battery use.
8. **Unplug the cable.** The app switches to Wi-Fi by itself (*"Cable unplugged — continuing over Wi-Fi"*). Everything works the same, including installing.

The headset is now remembered: its name, model and Wi-Fi address are saved on the PC.

### 3. Every day, and after a headset restart

- **Headset was not restarted:** just start Quest Apps. It finds the remembered headset on Wi-Fi and connects by itself, with no cable.
- **Headset was restarted or powered off:** the headset turns Wi-Fi ADB off on every restart. Plug in the cable, press **Enable Wi-Fi**, and unplug. You do **not** have to confirm "Allow USB debugging" again.
- **Headset got a new IP address** (for example after a router restart): press **Find on Wi-Fi**. It scans the local network for headsets that have Wi-Fi ADB on.

### 4. Main window

**Toolbar**

| Control | What it does |
|---|---|
| **Refresh** | Reloads apps, storage and large files from the selected headset. |
| **Headset list** | Headsets connected now, plus ones seen before. Labels: *USB*, *Wi-Fi*, *USB + Wi-Fi*, *nearby* (answers on Wi-Fi, connects when picked), *not found*. |
| **Find on Wi-Fi** | Searches the local network for headsets with Wi-Fi ADB on. |
| **Multi-headset…** | Opens the window for controlling many headsets at once (see section 6). |
| **Enable Wi-Fi / Disconnect Wi-Fi** | Turns Wi-Fi ADB on for a headset connected by cable, or drops the Wi-Fi connection. |
| **MTP** | Switches USB to MTP (Media Transfer Protocol), so file explorers on your computer can browse the headset over the cable. Turning it off returns USB to ADB only. ADB keeps working in both modes. On macOS, browsing MTP needs an app such as OpenMTP or Android File Transfer; on Linux, most file managers open MTP devices directly. |
| **Keep awake** | The headset stays on when it is taken off. A headset restart turns this off again. |
| **Sort** | Sorts apps by size or by name. |

**Storage bar.** Shows how full the headset is: store apps are blue, sideloaded apps orange, app data and the system grey. Point at an app in the list to highlight it on the bar.

**Tabs**

- **Store — official / Sideloaded — unknown sources.** Every app with its icon, version, size and source. The search box above the list filters it by app name or package name. Each row has buttons:
  - **Launch** starts the app in the headset. While the app runs, the row shows *● running*, and the buttons change to **Stop** and **Lock**.
  - **Lock** turns on kiosk mode (see section 5). **Unlock** ends it.
  - **Uninstall** removes the app and its data, after asking first.
- **Large files (app data).** Files of 50 MB or more in shared storage: downloads, videos, game data. **Delete** removes a file after asking first. A game may download deleted data again.
- **Install apps.**
  1. Drag `.apk` files or whole game folders into the window, or use **Choose APK…** / **Choose folder…**. You can also drag an APK onto `QuestApps.exe`.
  2. OBB game data is found automatically: a folder named after the package, or `main.*.<package>.obb` files next to the APK. It is copied to `/sdcard/Android/obb/<package>/`.
  3. Each entry shows whether it is a **new app** or an **update** of an installed version.
  4. Press **Install**. Progress and speed are shown for each app. **Cancel** stops the queue.
     When an app is installed, its row gets **Launch** (it turns into **Stop** while the app runs, to close it) and **Install again**, which installs the same APK (and OBB) once more, for example after an uninstall.
  5. **Grant all permissions** gives the app every permission it asks for, so the headset does not ask later.
  6. Two cases ask before going ahead:
     - the APK is signed with a different key than the installed version → *Uninstall & install* (the app's saved data is lost);
     - the APK is an older version than the installed one → *Downgrade*.
  7. Split-APK bundles (`.xapk`, `.apks`, `.apkm`) are not supported.

### 5. Lock (kiosk mode)

**Lock** pins a running app:

- other apps cannot be opened,
- if someone opens the Meta menu (Meta button or hand gesture), the app is brought back on top within about a second,
- if someone quits the app, it is started and locked again within a few seconds,
- if the pin is lifted in the headset, it is put back.

Limitations:

- The Meta menu **cannot be fully blocked** this way. It can flash for a moment. Fully disabling the Meta button and system gestures needs a headset managed by a company: Meta Horizon Managed Services, ManageXR or ArborXR, with the headset enrolled in an organization.
- Guarding needs **Quest Apps running on the PC and connected** to the headset. Use Wi-Fi and Keep awake. With the PC app closed, the headset stays pinned, but an app that was quit or left is not brought back.
- When you close Quest Apps while headsets are locked, it asks: **Unlock & close** or **Keep locked**.

### 6. Multi-headset window

**What it is for:** VR arcades, classrooms, events, demo stations. In general, any time several headsets should run the same app and stay locked in it, controlled from one PC.

**Setup:** pair **every** headset once as described in section 2 (cable → Allow USB debugging → Enable Wi-Fi). Then they can all run on Wi-Fi at the same time.

**Use it:**

1. In the main window, press **Multi-headset…**. Remembered headsets that answer on Wi-Fi are connected automatically.
2. **Name the headsets:** click the name field (for example *"Quest 3 •0402"*) and type something like *"Station 1"*. The name is saved and also shown in the main window.
3. **Tick** the headsets to control, or use **Select all** / **Select none**. Each row shows the connection, battery (⚡ while charging), the app on top (*Now*), the kiosk state and the result of the last action.
4. **Pick an app** on the right. The list shows apps on the selected headsets; *on all 4* in green, or *on 3 of 4* in yellow. Below the list it says which selected headsets do not have the app.
5. Press an action. It runs **on all selected headsets at the same time**:

| Action | What happens |
|---|---|
| **Launch** | Starts the app. Headsets without the app are skipped. |
| **Launch & Lock** | Starts the app and locks it (kiosk). A headset locked to another app is unlocked first. |
| **Unlock** | Ends the kiosk. |
| **Stop** | Unlocks and closes the app. |
| **Keep awake On / Off** | Headsets stay on when taken off, or sleep normally. |

**Installing on many headsets:** press **Install APK…** or **Install folder…** (or drop APK files and game folders on the window). The app shows what it found and on which headsets, and after you confirm it installs everything **on all selected headsets in parallel**, including OBB game data. Progress is shown next to each headset. All permissions the apps ask for are granted. A headset that already has a newer version, or a copy signed with a different key, is skipped with a note; downgrade or replace that one from the main window. **Cancel install** stops all uploads.

The result for each headset (with the time) appears next to it, and a summary appears at the top, for example *"done on 3 headset(s), 1 skipped"*.

Locks are shared between both windows: a headset locked here shows **Unlock** in the main window too, and the other way round.

### 7. Troubleshooting

| Problem | What to do |
|---|---|
| *No headset* | Check that the cable carries data. Wake the headset. Try another USB port. Close SideQuest or MQDH. Press Refresh. |
| *unauthorized* | Put the headset on and accept "Allow USB debugging" (tick *Always allow*). |
| Linux: *no permissions* | Install the udev rule (see *Installing on Linux*), then unplug and plug the cable back in. |
| macOS: the app closes at once, or "cannot be opened" | Run the two Terminal commands from *Installing on macOS*. |
| *adb not found* (macOS/Linux) | Install adb (see the install steps above). |
| Wi-Fi: *Could not connect / not found* | The PC and headset must be on the same network, not a guest network. Turn off any VPN and check the firewall. If the headset was restarted, turn Wi-Fi ADB on again by cable. |
| Wi-Fi keeps dropping | Turn on **Keep awake**. The headset turns Wi-Fi off when it sleeps. |
| New IP address | **Find on Wi-Fi**. |
| Install error | The message explains it (*different key*, *newer version installed*, *not enough space*, *wrong CPU*…). |
| Kiosk app not coming back | Quest Apps must be running and connected. Check the status line and the headset row. |

### 8. Files and extras

- Settings and cache live in `%AppData%\QuestApps\` on Windows, and in `~/.config/QuestApps/` on macOS and Linux:
  - `cache\` holds app names and icons,
  - `headsets.json` holds remembered headsets, their names and IP addresses,
  - `selected-headset.txt` holds the headset selected in the main window.
- Command-line helpers, mainly for testing:
  - `QuestApps.exe <file.apk|folder>…` opens the app with these files already in the install queue.
  - `QuestApps.exe --scan <paths>` shows what would be queued, without installing.
  - `QuestApps.exe --label-file <file.apk>` reads the name and version from a local APK.

---

<a id="polski"></a>
## Polski

Quest Apps to program na komputer (Windows, macOS, Linux) do gogli Meta Quest (testowany na Quest 3). Łączy się z goglami przez ADB, kablem USB albo przez Wi-Fi, i pozwala:

- zobaczyć wszystkie zainstalowane aplikacje z rozmiarem, wersją i ikoną, a także zajętość pamięci,
- odinstalowywać aplikacje i usuwać duże, zalegające pliki,
- instalować APK (razem z danymi gier OBB) przez przeciągnięcie,
- uruchamiać i zamykać aplikacje z komputera,
- **blokować** aplikację (tryb kiosku), tak żeby nie dało się z niej wyjść,
- sterować **wieloma goglami naraz**: instalować APK oraz uruchamiać, blokować i odblokowywać tę samą aplikację na wszystkich.

### 1. Wymagania

- Komputer z jednym z systemów:
  - **Windows 10/11 x64.** Niczego nie trzeba doinstalowywać: `adb` jest w folderze `adb\` obok `QuestApps.exe`. Trzymaj je razem.
  - **macOS 12 lub nowszy**, Apple Silicon albo Intel. Trzeba zainstalować adb, patrz niżej.
  - **Linux x64 lub ARM64.** Trzeba zainstalować adb, patrz niżej.
- Gogle Quest z włączonym **trybem dewelopera**.
- Kabel USB-C do przesyłania danych (nie sam ładujący), potrzebny raz na każde gogle.
- Do Wi-Fi: komputer i gogle w **tej samej sieci**. Sieci dla gości i sieci izolujące urządzenia od siebie to blokują.

#### Instalacja na macOS

1. Rozpakuj właściwy plik:
   - `QuestApps-4.3-macos-arm64.zip` dla Apple Silicon (M1–M4),
   - `QuestApps-4.3-macos-x64.zip` dla Maców z procesorem Intel.
2. Przenieś `QuestApps.app` do **Aplikacji**.
3. Zainstaluj adb przez Homebrew: `brew install --cask android-platform-tools`. Działa też adb z Android Studio.
4. Aplikacja nie jest podpisana przez dewelopera Apple, więc przed **pierwszym** uruchomieniem wpisz raz w Terminalu poniższe polecenia. Druga linia jest wymagana na Apple Silicon; bez niej aplikacja od razu się zamyka.
   ```bash
   xattr -dr com.apple.quarantine /Applications/QuestApps.app
   codesign --force --deep -s - /Applications/QuestApps.app
   ```
5. Uruchamiaj Quest Apps normalnie. Na Macu nie trzeba sterownika USB.

#### Instalacja na Linuksie

1. Rozpakuj: `tar xzf QuestApps-4.3-linux-x64.tar.gz` (plik `-linux-arm64` jest dla płytek ARM).
2. Zainstaluj adb:
   - Debian/Ubuntu: `sudo apt install adb`
   - Fedora: `sudo dnf install android-tools`
   - Arch: `sudo pacman -S android-tools`
3. Daj adb dostęp do gogli przez USB, potem odepnij i podepnij kabel ponownie. Bez tego gogle są widoczne jako *no permissions*.
   ```bash
   sudo cp 99-meta-quest.rules /etc/udev/rules.d/ && sudo udevadm control --reload-rules
   ```
4. Uruchom aplikację poleceniem `./QuestApps`.
5. *(Opcjonalnie)* Skrót w menu: skopiuj `questapps.desktop` do `~/.local/share/applications/` i `questapps.png` do `~/.local/share/icons/`. W pliku `.desktop` ustaw `Exec=` na pełną ścieżkę do `QuestApps`.

Na macOS i Linuksie wszędzie, gdzie ta instrukcja mówi `QuestApps.exe`, chodzi o Quest Apps / `QuestApps`.

### 2. Pierwsze podłączenie nowych gogli (parowanie)

Każde nowe gogle trzeba **raz podłączyć kablem**. Wtedy gogle uczą się ufać Twojemu komputerowi. Nie da się włączyć Wi-Fi dla gogli, które nigdy nie były podłączone kablem.

1. **Włącz tryb dewelopera.** Otwórz aplikację Meta Horizon w telefonie → Urządzenia → wybierz gogle → Ustawienia gogli → Tryb dewelopera → włącz. Zrestartuj gogle. Nazwy menu mogą się różnić w zależności od wersji aplikacji. Meta może też najpierw poprosić o założenie lub dołączenie do organizacji deweloperskiej na swojej stronie.
2. **Zamknij SideQuest i Meta Quest Developer Hub**, jeśli są otwarte. Każdy z nich ma własne adb, a dwie wersje adb działające jednocześnie wzajemnie się rozłączają.
3. Uruchom `QuestApps.exe` i **podłącz gogle kablem USB**. Gogle muszą być wybudzone.
4. **Załóż gogle.** Pojawi się pytanie *„Zezwolić na debugowanie USB?”*. Zaznacz **„Zawsze zezwalaj z tego komputera”** i naciśnij **Zezwól**.
   - Dopóki tego nie zrobisz, lista gogli pokazuje je jako *unauthorized*.
   - Z zaznaczonym „Zawsze zezwalaj” to pytanie dla tego komputera już nigdy nie wróci.
5. Naciśnij **Refresh**. Gogle pojawią się jako *„Quest 3 •0402 — USB”* i wczytają się aplikacje.
6. **Włącz Wi-Fi.** Naciśnij **Enable Wi-Fi** w górnym pasku. Po kilku sekundach lista pokaże *„USB + Wi-Fi”*, a status napisze, że można odpiąć kabel.
7. *(Zalecane)* Zaznacz **Keep awake**. Gogle zdjęte z głowy po chwili zasypiają i wtedy Wi-Fi się rozłącza. Keep awake temu zapobiega, kosztem większego zużycia baterii.
8. **Odepnij kabel.** Aplikacja sama przejdzie na Wi-Fi (*„Cable unplugged — continuing over Wi-Fi”*). Wszystko działa tak samo, łącznie z instalowaniem.

Gogle są teraz zapamiętane: ich nazwa, model i adres Wi-Fi są zapisane na komputerze.

### 3. Na co dzień i po restarcie gogli

- **Gogle nie były restartowane:** po prostu uruchom Quest Apps. Program sam znajdzie zapamiętane gogle w Wi-Fi i połączy się z nimi, bez kabla.
- **Gogle zostały zrestartowane albo wyłączone:** przy każdym restarcie gogle wyłączają Wi-Fi ADB. Podłącz kabel, naciśnij **Enable Wi-Fi** i odepnij. **Nie** trzeba ponownie zatwierdzać „Zezwól na debugowanie USB”.
- **Gogle dostały nowy adres IP** (np. po restarcie routera): naciśnij **Find on Wi-Fi**. Program przeszuka sieć lokalną w poszukiwaniu gogli z włączonym Wi-Fi ADB.

### 4. Główne okno

**Górny pasek**

| Element | Do czego służy |
|---|---|
| **Refresh** | Ponownie wczytuje aplikacje, pamięć i duże pliki z wybranych gogli. |
| **Lista gogli** | Gogle podłączone teraz oraz te widziane wcześniej. Opisy: *USB*, *Wi-Fi*, *USB + Wi-Fi*, *nearby* (w zasięgu Wi-Fi, połączy się po wybraniu), *not found*. |
| **Find on Wi-Fi** | Przeszukuje sieć lokalną w poszukiwaniu gogli z włączonym Wi-Fi ADB. |
| **Multi-headset…** | Otwiera okno do sterowania wieloma goglami naraz (punkt 6). |
| **Enable Wi-Fi / Disconnect Wi-Fi** | Włącza Wi-Fi ADB dla gogli podłączonych kablem albo rozłącza połączenie Wi-Fi. |
| **MTP** | Przełącza USB w tryb MTP (Media Transfer Protocol), dzięki czemu eksplorator plików na komputerze widzi pamięć gogli przez kabel. Wyłączenie przywraca USB do trybu „tylko ADB”. ADB działa w obu trybach. Na macOS do przeglądania MTP potrzebna jest aplikacja, np. OpenMTP albo Android File Transfer; na Linuksie większość menedżerów plików otwiera urządzenia MTP bezpośrednio. |
| **Keep awake** | Gogle nie zasypiają po zdjęciu z głowy. Restart gogli wyłącza tę opcję. |
| **Sort** | Sortuje aplikacje według rozmiaru albo nazwy. |

**Pasek pamięci.** Pokazuje zajętość gogli: aplikacje ze sklepu na niebiesko, wgrane z zewnątrz (sideloaded) na pomarańczowo, dane aplikacji i system na szaro. Najechanie na aplikację na liście podświetla ją na pasku.

**Zakładki**

- **Store — official / Sideloaded — unknown sources.** Wszystkie aplikacje z ikoną, wersją, rozmiarem i źródłem. Pole wyszukiwania nad listą filtruje ją po nazwie aplikacji albo pakietu. W każdym wierszu są przyciski:
  - **Launch** uruchamia aplikację w goglach. Gdy aplikacja działa, wiersz pokazuje *● running*, a przyciski zmieniają się na **Stop** i **Lock**.
  - **Lock** włącza tryb kiosku (punkt 5). **Unlock** go wyłącza.
  - **Uninstall** usuwa aplikację razem z jej danymi, po potwierdzeniu.
- **Large files (app data).** Pliki od 50 MB wzwyż we wspólnej pamięci: pobrane pliki, filmy, dane gier. **Delete** usuwa plik po potwierdzeniu. Gra może ponownie pobrać usunięte dane.
- **Install apps.**
  1. Przeciągnij pliki `.apk` albo całe foldery z grami do okna lub użyj **Choose APK…** / **Choose folder…**. Można też przeciągnąć APK na `QuestApps.exe`.
  2. Dane gier OBB są wykrywane automatycznie: folder o nazwie pakietu albo pliki `main.*.<pakiet>.obb` obok APK. Trafiają do `/sdcard/Android/obb/<pakiet>/`.
  3. Każda pozycja pokazuje, czy to **nowa aplikacja**, czy **aktualizacja** zainstalowanej wersji.
  4. Naciśnij **Install**. Przy każdej aplikacji widać postęp i prędkość. **Cancel** zatrzymuje kolejkę.
     Przy zainstalowanej aplikacji pojawiają się **Launch** (gdy aplikacja działa, zmienia się w **Stop**, żeby ją zamknąć) oraz **Install again**, które instaluje ten sam APK (i OBB) jeszcze raz, np. po odinstalowaniu.
  5. **Grant all permissions** nadaje aplikacji wszystkie uprawnienia, o które prosi, więc gogle nie pytają o nie później.
  6. W dwóch przypadkach program pyta przed kontynuacją:
     - APK jest podpisane innym kluczem niż zainstalowana wersja → *Uninstall & install* (zapisane dane aplikacji przepadną);
     - APK to starsza wersja niż zainstalowana → *Downgrade*.
  7. Paczki dzielone (`.xapk`, `.apks`, `.apkm`) nie są obsługiwane.

### 5. Lock (tryb kiosku)

**Lock** przypina uruchomioną aplikację:

- nie da się otworzyć innych aplikacji,
- gdy ktoś otworzy menu Meta (przyciskiem Meta albo gestem dłoni), aplikacja wraca na wierzch w ciągu około sekundy,
- gdy ktoś zamknie aplikację, uruchamia się ona ponownie i zostaje znowu zablokowana w ciągu kilku sekund,
- gdy ktoś zdejmie przypięcie w goglach, zostaje ono założone z powrotem.

Ograniczenia:

- Tym sposobem **nie da się całkowicie zablokować** menu Meta. Może na chwilę mignąć. Pełne wyłączenie przycisku Meta i gestów systemowych wymaga gogli zarządzanych przez firmę: Meta Horizon Managed Services, ManageXR albo ArborXR, z goglami zapisanymi w organizacji.
- Pilnowanie wymaga, żeby **Quest Apps działał na komputerze i był połączony** z goglami. Używaj Wi-Fi i Keep awake. Po zamknięciu programu gogle zostają przypięte, ale zamknięta lub opuszczona aplikacja nie zostanie już przywrócona.
- Przy zamykaniu Quest Apps z zablokowanymi goglami program pyta: **Unlock & close** albo **Keep locked**.

### 6. Okno wielu gogli (Multi-headset)

**Do czego służy:** salony VR, klasy, eventy, stanowiska demo. Ogólnie wszędzie tam, gdzie kilka gogli ma uruchomić tę samą aplikację i w niej zostać, sterowanych z jednego komputera.

**Przygotowanie:** sparuj **każde** gogle raz, tak jak w punkcie 2 (kabel → Zezwól na debugowanie USB → Enable Wi-Fi). Potem wszystkie mogą działać jednocześnie przez Wi-Fi.

**Jak używać:**

1. W głównym oknie naciśnij **Multi-headset…**. Zapamiętane gogle, które odpowiadają w Wi-Fi, połączą się automatycznie.
2. **Nazwij gogle:** kliknij pole z nazwą (np. *„Quest 3 •0402”*) i wpisz np. *„Stanowisko 1”*. Nazwa zapisuje się na stałe i jest widoczna także w głównym oknie.
3. **Zaznacz** gogle, którymi chcesz sterować, albo użyj **Select all** / **Select none**. Każdy wiersz pokazuje połączenie, baterię (⚡ podczas ładowania), aplikację na wierzchu (*Now*), stan kiosku i wynik ostatniej akcji.
4. **Wybierz aplikację** po prawej. Lista pokazuje aplikacje z zaznaczonych gogli: *on all 4* na zielono albo *on 3 of 4* na żółto. Pod listą widać, na których zaznaczonych goglach danej aplikacji brakuje.
5. Naciśnij akcję. Wykonuje się **na wszystkich zaznaczonych goglach jednocześnie**:

| Akcja | Co się dzieje |
|---|---|
| **Launch** | Uruchamia aplikację. Gogle bez tej aplikacji są pomijane. |
| **Launch & Lock** | Uruchamia aplikację i ją blokuje (kiosk). Gogle zablokowane na innej aplikacji są najpierw odblokowywane. |
| **Unlock** | Wyłącza kiosk. |
| **Stop** | Odblokowuje i zamyka aplikację. |
| **Keep awake On / Off** | Gogle nie zasypiają po zdjęciu z głowy albo zasypiają normalnie. |

**Instalacja na wielu goglach:** naciśnij **Install APK…** albo **Install folder…** (albo przeciągnij pliki APK i foldery gier na okno). Program pokaże, co znalazł i na których goglach zainstaluje, a po potwierdzeniu instaluje wszystko **na wszystkich zaznaczonych goglach naraz**, razem z danymi OBB. Postęp widać przy każdych goglach. Aplikacje dostają wszystkie uprawnienia, o które proszą. Gogle, które mają już nowszą wersję albo kopię podpisaną innym kluczem, są pomijane z komentarzem; taką instalację zrób w głównym oknie. **Cancel install** przerywa wszystkie wysyłki.

Wynik dla każdych gogli (z godziną) pojawia się przy nich, a podsumowanie na górze, np. *„done on 3 headset(s), 1 skipped”*.

Blokady są wspólne dla obu okien: gogle zablokowane tutaj mają przycisk **Unlock** także w głównym oknie, i odwrotnie.

### 7. Rozwiązywanie problemów

| Problem | Co zrobić |
|---|---|
| *No headset* | Sprawdź, czy kabel przesyła dane. Wybudź gogle. Spróbuj innego portu USB. Zamknij SideQuest albo MQDH. Naciśnij Refresh. |
| *unauthorized* | Załóż gogle i zatwierdź „Zezwól na debugowanie USB” (zaznacz *Zawsze zezwalaj*). |
| Linux: *no permissions* | Zainstaluj regułę udev (patrz *Instalacja na Linuksie*), potem odepnij i podepnij kabel. |
| macOS: aplikacja od razu się zamyka albo „nie można otworzyć” | Wpisz dwa polecenia z *Instalacji na macOS*. |
| *adb not found* (macOS/Linux) | Zainstaluj adb (patrz kroki instalacji powyżej). |
| Wi-Fi: *Could not connect / not found* | Komputer i gogle muszą być w tej samej sieci, nie w sieci dla gości. Wyłącz VPN i sprawdź zaporę. Jeśli gogle były restartowane, włącz Wi-Fi ADB ponownie przez kabel. |
| Wi-Fi ciągle się rozłącza | Włącz **Keep awake**. Gogle wyłączają Wi-Fi, gdy zasypiają. |
| Nowy adres IP | **Find on Wi-Fi**. |
| Błąd instalacji | Komunikat wyjaśnia przyczynę (*inny klucz*, *zainstalowana nowsza wersja*, *brak miejsca*, *inny procesor*…). |
| Aplikacja w kiosku nie wraca | Quest Apps musi działać i być połączony. Sprawdź linię statusu i wiersz gogli. |

### 8. Pliki i dodatki

- Ustawienia i pamięć podręczna są w `%AppData%\QuestApps\` na Windows, a w `~/.config/QuestApps/` na macOS i Linuksie:
  - `cache\` przechowuje nazwy i ikony aplikacji,
  - `headsets.json` przechowuje zapamiętane gogle, ich nazwy i adresy IP,
  - `selected-headset.txt` przechowuje gogle wybrane w głównym oknie.
- Polecenia z linii poleceń, głównie do testów:
  - `QuestApps.exe <plik.apk|folder>…` otwiera program z tymi plikami od razu w kolejce instalacji.
  - `QuestApps.exe --scan <ścieżki>` pokazuje, co trafiłoby do kolejki, bez instalowania.
  - `QuestApps.exe --label-file <plik.apk>` odczytuje nazwę i wersję z lokalnego APK.
