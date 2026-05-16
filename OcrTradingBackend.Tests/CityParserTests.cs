using Microsoft.VisualStudio.TestTools.UnitTesting;
using OcrTradingBackend.Services;

namespace OcrTradingBackend.Tests;

[TestClass]
public sealed class CityParserTests
{
    [TestMethod]
    public void ParsesStPetersburgWithPeriod()
    {
        var parser = new CityParser(new TestCityCatalog(
            new CityDefinition("St. Petersburg", [], "", "", "")));

        var city = parser.TryParse("St. Petersburg", minLetters: 3);

        Assert.AreEqual("St. Petersburg", city);
    }

    private sealed class TestCityCatalog : ICityCatalog
    {
        private readonly IReadOnlyList<CityDefinition> _cities;

        public TestCityCatalog(params CityDefinition[] cities)
        {
            _cities = cities;
        }

        public IReadOnlyList<CityDefinition> GetAll() => _cities;

        public CityDefinition? FindByName(string name)
        {
            return _cities.FirstOrDefault(city =>
                string.Equals(city.Name, name, StringComparison.OrdinalIgnoreCase) ||
                city.Aliases.Any(alias => string.Equals(alias, name, StringComparison.OrdinalIgnoreCase)));
        }

        public IReadOnlyList<string> GetMainRegions() => [];

        public IReadOnlyList<string> GetSubRegions(string? mainRegion = null) => [];

        public IReadOnlyList<string> GetSeaTradeRegions(string? mainRegion = null, string? subRegion = null) => [];

        public SaveCityResult AddCity(SaveCityRequest request) => throw new NotSupportedException();

        public SaveCityResult UpdateCity(string name, SaveCityRequest request) => throw new NotSupportedException();

        public SaveCityResult DeleteCity(string name) => throw new NotSupportedException();

        public string ExportCsv() => throw new NotSupportedException();

        public Task<CityCsvImportResult> ImportCsvAsync(Stream stream, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
