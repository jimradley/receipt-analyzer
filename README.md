# Receipt Analyzer

A .NET 9 tool for extracting health, price, and provenance data from UK supermarket receipts. 

This project uses LLMs (specifically Claude) to transform receipt photos into a structured ledger, highlighting ultra-processed foods (NOVA levels), US-owned brands, and potential savings at other retailers.

## Features

- **Receipt Parsing:** Converts images (JPEG, PNG, HEIC) into structured JSON including items, unit prices, and retailers.
- **NOVA Classification:** Automatically identifies the processing level of food items (NOVA 1–4).
- **Brand Provenance:** Flags brands owned by US-headquartered parent companies.
- **Price Comparison:** Real-time checking of major UK supermarkets (Sainsbury's, Asda, Morrisons, Waitrose, Ocado, Aldi, Lidl) to find better prices. Tesco is deliberately excluded.
- **UK Seasonality:** Checks fresh produce against a UK growing calendar to identify out-of-season imports.
- **Persistent Ledger:** Maintains a local state and generates Markdown reports (`alternatives.md`, `buy-elsewhere.md`) for long-term shopping habits.

## Architecture

- **Backend:** ASP.NET Core 9 Minimal API.
- **Frontend:** Blazor WebAssembly PWA.
- **AI Integration:** Anthropic Claude API (using `web-search` for price checks and `prompt-caching` for efficiency).
- **Storage:** File-based JSON ledger with Markdown rendering.
- **Image Processing:** ImageMagick for HEIC to JPEG conversion.

## Getting Started

### Prerequisites
- .NET 9 SDK
- An Anthropic API Key (with access to Claude 3.5 Sonnet or similar)

### Configuration
Set the following environment variable or add it to `appsettings.json`:
- `ANTHROPIC_API_KEY`: Your API key.

The system expects a `rules.txt` file (configured in `AgentOptions`) containing your specific dietary or brand preferences for the classification logic.

### Running the App
1. Clone the repository.
2. Run the API project:
   ```powershell
   dotnet run --project src/ReceiptAnalyzer.Api
   ```
3. Access the PWA in your browser (usually `http://localhost:5000`).

## Project Structure

- `src/ReceiptAnalyzer.Agent`: AI logic, prompts, and API clients.
- `src/ReceiptAnalyzer.Api`: Web API and image processing.
- `src/ReceiptAnalyzer.Ledger`: Ledger logic, merging, and file persistence.
- `src/ReceiptAnalyzer.Pwa`: Blazor frontend for mobile/desktop.
- `src/ReceiptAnalyzer.Reports`: Markdown report generation.

## License
MIT
