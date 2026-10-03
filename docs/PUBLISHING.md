# Install locally, take screenshots and publish VisaryPDF to the Microsoft Store

Everything below is done in **Visual Studio 2026**. VisaryPDF targets .NET 10, which Visual Studio supports from version 2026 (18.0) on; Visual Studio 2022 cannot build it ([SDK and Visual Studio versions](https://learn.microsoft.com/en-us/dotnet/core/porting/versioning-sdk-msbuild-vs)).

The solution is `VisaryPDF.sln`; the app project (`src/VisaryPDF.App`) is packaged as MSIX.

---

## Part 0. Set up the PC (once)

1. **Install Visual Studio 2026** (Community is free) from https://visualstudio.microsoft.com. In the installer, on **Workloads**, tick:
   - **WinUI application development** (Windows App SDK, MSIX packaging tools)
   - **.NET desktop development**
   - **Desktop development with C++**. Needed because the release build is compiled ahead of time (Native AOT), which uses the C++ linker ([Native AOT prerequisites](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)).
   Then on **Individual components** search "ARM64" and tick **MSVC ARM64/ARM64EC build tools (Latest)** (only needed for the ARM64 package).
2. **Install the .NET 10 SDK** if the installer did not already add it: https://dotnet.microsoft.com/download/dotnet/10.0 (x64 installer).
3. **Turn on Developer Mode**: Windows Settings > System > For developers (Windows 10: Update & Security > For developers) > **Developer Mode** on. Visual Studio needs it to install the app on this PC.
4. **Get the code**: in Visual Studio, **Clone a repository**, paste the GitHub address of the repository, choose a folder, **Clone**. When it finishes, open **VisaryPDF.sln** (File > Open > Project/Solution, or double click it in Solution Explorer's folder view).
5. **First build**: wait until Visual Studio finishes restoring packages (status bar), then **Build > Build Solution** (Ctrl+Shift+B). The first build also downloads the PDF engine (PDFium) for x64 and ARM64 automatically. The Output window should end with `Build succeeded`.
6. **Optional, run the tests**: **Test > Run All Tests**. The Core and Platform tests should pass.

---

## Part 1. Install a local copy (for trying the app and taking screenshots)

1. Open `VisaryPDF.sln` in Visual Studio.
2. On the toolbar choose **Release** and **x64**. The start button shows **VisaryPDF (Package)**.
3. Menu **Build > Deploy Solution**.
   Visual Studio builds the app and installs it on this PC as a package. VisaryPDF now appears in the Start menu, opens PDFs from Explorer ("Open with" > VisaryPDF) and stays installed after you close Visual Studio.
4. Start **VisaryPDF** from the Start menu.

To remove it later: Start menu, right click VisaryPDF, **Uninstall**.
(F5 does the same deployment and additionally attaches the debugger.)

### Optional: an installable .msix file for another PC

1. In Solution Explorer right click **VisaryPDF.App** > **Package and Publish** > **Create App Packages...** (older versions: **Publish** > **Create App Packages...**).
2. Choose **Sideloading**, keep **Enable automatic updates** off, **Next**.
3. Signing: **Create...** a test certificate (publisher `CN=VisaryPDF`), **Next**.
4. Select **x64** (and **ARM64** if wanted), Release, publish profile **msix-x64** / **msix-ARM64**, then **Create**.
5. The output folder (`AppPackages`) contains the package (`VisaryPDF.App_1.0.0.0_x64.msixbundle`, or `.msix`) and a `.cer` file. On the other PC, first install the `.cer` into **Local Machine > Trusted People**, then double click the `.msixbundle` (or `.msix`).

---

## Part 2. Take the Store screenshots

Store rules for desktop apps ([screenshots and images](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/screenshots-and-images)):

- PNG, **1366 x 768 or larger** (up to 3840 x 2160). At least 1, up to 10.
- Keep important content in the top two thirds (the Store can overlay text on the bottom third).

Suggested set, each with a real multi page PDF open:

1. Reading a document, tool bar open, thumbnails shown (Light theme).
2. The same in Dark or Black theme.
3. Fill and sign: a form being filled, the signature dialog open.
4. Editing: Edit text or Add text with a comment in the comments pane.
5. OCR: a scanned page after "Make searchable", with search hits highlighted.
6. Menu > Convert opened (Word, text, images).

How to capture at a size the Store accepts:

1. Set the display to 1920 x 1080 (or larger) and maximize VisaryPDF (double click the title bar).
2. Press **Win + Shift + S**, choose **Window mode**, click the VisaryPDF window.
3. In the Snipping Tool notification, **Save** as PNG.

---

## Part 3. Publish to the Microsoft Store

### 3.1 Partner Center (once)

1. Sign in at https://partner.microsoft.com/dashboard and create a developer account ([get started](https://learn.microsoft.com/en-us/windows/apps/publish/get-started)).
2. **Apps and games > New product > MSIX or PWA app**, and reserve the name **VisaryPDF** (if it is taken, reserve another and change `DisplayName` in `Package.appxmanifest` to match).

### 3.2 Connect the project to the reserved name

1. Right click **VisaryPDF.App** > **Package and Publish** > **Associate App with the Store...** (older versions: **Publish** > **Associate App with the Store**).
2. Sign in with the Partner Center account and pick **VisaryPDF**.
3. Visual Studio writes the reserved **Package name**, **Publisher** (`CN=...`) and **Publisher display name** into `Package.appxmanifest`. Commit that change.

### 3.3 Build the upload package

1. Right click **VisaryPDF.App** > **Package and Publish** > **Create App Packages...**
2. Choose **Microsoft Store as VisaryPDF** (the associated name), **Next**.
3. Version: 1.0.0.0 for the first release (the last number must stay 0 for the Store). Raise it for every update.
4. Architectures: **x64** and **ARM64**, configuration **Release**, publish profiles **msix-x64** and **msix-ARM64**. **Generate app bundle: Always**.
5. **Create**. When it finishes, run the **Windows App Certification Kit** that Visual Studio offers and fix anything it reports.
6. The output folder contains a file ending in `_bundle.msixupload`, for example `VisaryPDF.App_1.0.0.0_x64_arm64_bundle.msixupload`. This is the file you upload.

The Store signs the package itself; you do not need a code signing certificate.

### 3.4 Create the submission

In Partner Center open VisaryPDF > **Start your submission** ([create an app submission](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/create-app-submission)):

1. **Pricing and availability**: markets, free or paid, release date.
2. **Properties**: category **Productivity**. Privacy policy: add one if Partner Center asks (VisaryPDF itself sends no data anywhere; everything, including OCR, runs on the PC).
3. **Age ratings**: fill in the questionnaire (a PDF tool normally rates 3+ / Everyone).
4. **Packages**: upload the `.msixupload` from 3.3.
5. **Store listings**: description, short description, the screenshots from Part 2, app icon (use `src/VisaryPDF.App/Assets/Square44x44Logo.targetsize-256_altform-unplated.png`, or a 300 x 300 PNG made from `assets/icon/visarypdf.png`).
6. **Submission options > Restricted capabilities**: the app declares `runFullTrust` (normal for desktop apps). Explanation you can use: "VisaryPDF is a WinUI 3 desktop app built with the Windows App SDK. It runs as a full trust desktop process to open, edit, print and save PDF files the user chooses, and to use the Windows OCR engine."
7. **Submit for certification**. Certification usually takes a few days; Partner Center e-mails the result.

### 3.5 Updates

Raise the version (for example 1.0.1.0) in **Create App Packages**, build the new `.msixupload`, and in Partner Center choose **Update** on the submission and replace the package.

---

## Checked by CI

Every push runs a job that builds the x64 MSIX exactly this way (msix-x64 profile, Native AOT), signs it with a throwaway test certificate, installs it on a clean Windows runner (as package `VisaryPDF_1.0.0.0_x64`) and starts it. The test package and its certificate are attached to the run as **VisaryPDF-x64-msix-test**.
