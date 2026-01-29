using LiteDB;
using Microsoft.Extensions.Logging;
using Valt.Core.Common;
using Valt.Core.Modules.Budget.Accounts;
using Valt.Core.Modules.Budget.Categories;
using Valt.Infra.Modules.Budget.Accounts;
using Valt.Infra.Modules.Budget.Transactions;

namespace Valt.Infra.Modules.Reports.ExpensesByCategory;

internal class ExpensesByCategoryReport : IExpensesByCategoryReport
{
    private readonly ILogger<ExpensesByCategoryReport> _logger;

    public ExpensesByCategoryReport(ILogger<ExpensesByCategoryReport> logger)
    {
        _logger = logger;
    }

    public Task<ExpensesByCategoryData> GetAsync(DateOnly baseDate, DateOnlyRange displayRange, FiatCurrency currency, IExpensesByCategoryReport.Filter filter, IReportDataProvider provider)
    {
        if (provider.AllTransactions.Count == 0)
        {
            // Return empty result instead of throwing
            return Task.FromResult(new ExpensesByCategoryData
            {
                MainCurrency = currency,
                Items = new List<ExpensesByCategoryData.Item>()
            });
        }

        var calculator = new Calculator(currency, provider, displayRange.Start, displayRange.End, filter);

        try
        {
            return Task.FromResult(calculator.Calculate());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while calculating expenses by category");
            throw;
        }
    }

    private class Calculator
    {
        private const decimal SatoshisPerBitcoin = 100_000_000m;

        private readonly FiatCurrency _currency;
        private readonly IReportDataProvider _provider;
        private readonly DateOnly _startDate;
        private readonly DateOnly _endDate;
        private readonly IExpensesByCategoryReport.Filter _filter;

        public Calculator(
            FiatCurrency currency,
            IReportDataProvider provider,
            DateOnly startDate,
            DateOnly endDate,
            IExpensesByCategoryReport.Filter filter)
        {
            _currency = currency;
            _provider = provider;
            _startDate = startDate;
            _endDate = endDate;
            _filter = filter;
        }

        public ExpensesByCategoryData Calculate()
        {
            var categoryFiatTotals = new Dictionary<ObjectId, decimal>();

            var currentDate = _startDate.AddDays(-1);
            while (currentDate < _endDate)
            {
                currentDate = currentDate.AddDays(1);

                if (!_provider.AccountsByDate.TryGetValue(currentDate, out var accountsForDate))
                {
                    continue;
                }

                if (!_provider.TransactionsByDate.TryGetValue(currentDate, out var transactionsForDate))
                {
                    continue;
                }

                foreach (var accountId in accountsForDate)
                {
                    // If filter has account IDs, only include matching accounts; otherwise include all
                    if (_filter.AccountIds.Any() && !_filter.AccountIds.Contains(new AccountId(accountId.ToString())))
                        continue;

                    var account = _provider.Accounts[accountId];

                    var transactions = transactionsForDate.Where(x => x.FromAccountId == accountId &&
                        (x.Type == TransactionEntityType.Fiat || x.Type == TransactionEntityType.Bitcoin ||
                         (_filter.IncludeTransfers && IsTransferTransaction(x.Type))));

                    foreach (var transaction in transactions)
                    {
                        // If filter has category IDs, only include matching categories; otherwise include all
                        if (_filter.CategoryIds.Any() && !_filter.CategoryIds.Contains(new CategoryId(transaction.CategoryId.ToString())))
                            continue;

                        if (account.AccountEntityType == AccountEntityType.Bitcoin)
                        {
                            //only spending (negative amounts) or transfers
                            if (transaction.FromSatAmount > 0 && !IsTransferTransaction(transaction.Type))
                                continue;

                            if (!categoryFiatTotals.ContainsKey(transaction.CategoryId))
                                categoryFiatTotals[transaction.CategoryId] = 0;

                            var usdBitcoinPrice = _provider.GetUsdBitcoinPriceAt(currentDate);
                            var bitcoin = transaction.FromSatAmount.GetValueOrDefault() / SatoshisPerBitcoin;

                            // For transfers, we want to show outgoing transfers as expenses
                            if (IsTransferTransaction(transaction.Type) && transaction.Type == TransactionEntityType.BitcoinToBitcoin)
                            {
                                // For BitcoinToBitcoin transfers, consider FromSatAmount as outgoing if this is the source account
                                if (transaction.FromAccountId == accountId && transaction.FromSatAmount.HasValue && transaction.FromSatAmount.Value < 0)
                                {
                                    bitcoin = Math.Abs(transaction.FromSatAmount.Value) / SatoshisPerBitcoin;
                                }
                                else
                                {
                                    continue; // Skip incoming transfers
                                }
                            }
                            else if (IsTransferTransaction(transaction.Type) && transaction.Type == TransactionEntityType.BitcoinToFiat)
                            {
                                // For BitcoinToFiat transfers, consider FromSatAmount as outgoing if this is the source account
                                if (transaction.FromAccountId == accountId && transaction.FromSatAmount.HasValue && transaction.FromSatAmount.Value < 0)
                                {
                                    bitcoin = Math.Abs(transaction.FromSatAmount.Value) / SatoshisPerBitcoin;
                                }
                                else
                                {
                                    continue; // Skip incoming transfers
                                }
                            }
                            else
                            {
                                // For regular transactions, use absolute value for negative amounts
                                bitcoin = Math.Abs(bitcoin);
                            }

                            categoryFiatTotals[transaction.CategoryId] += _provider.GetFiatRateAt(currentDate, _currency) *
                                                                           (bitcoin * usdBitcoinPrice);
                        }
                        else
                        {
                            //only spending (negative amounts) or transfers
                            if (transaction.FromFiatAmount > 0 && !IsTransferTransaction(transaction.Type))
                                continue;

                            if (!categoryFiatTotals.ContainsKey(transaction.CategoryId))
                                categoryFiatTotals[transaction.CategoryId] = 0;

                            decimal amount = transaction.FromFiatAmount.GetValueOrDefault();
                            string currency = account.Currency;

                            // For transfers, we want to show outgoing transfers as expenses
                            if (IsTransferTransaction(transaction.Type) && transaction.Type == TransactionEntityType.FiatToFiat)
                            {
                                // For FiatToFiat transfers, consider FromFiatAmount as outgoing if this is the source account
                                if (transaction.FromAccountId == accountId && transaction.FromFiatAmount.HasValue && transaction.FromFiatAmount.Value < 0)
                                {
                                    amount = Math.Abs(transaction.FromFiatAmount.Value);
                                }
                                else
                                {
                                    continue; // Skip incoming transfers
                                }
                            }
                            else if (IsTransferTransaction(transaction.Type) && transaction.Type == TransactionEntityType.FiatToBitcoin)
                            {
                                // For FiatToBitcoin transfers, consider FromFiatAmount as outgoing if this is the source account
                                if (transaction.FromAccountId == accountId && transaction.FromFiatAmount.HasValue && transaction.FromFiatAmount.Value < 0)
                                {
                                    amount = Math.Abs(transaction.FromFiatAmount.Value);
                                }
                                else
                                {
                                    continue; // Skip incoming transfers
                                }
                            }
                            else
                            {
                                // For regular transactions, use absolute value for negative amounts
                                amount = Math.Abs(amount);
                            }

                            if (currency == _currency.Code)
                            {
                                categoryFiatTotals[transaction.CategoryId] += amount;
                            }
                            else
                            {
                                // Convert: source currency -> USD -> target currency
                                var accountCurrency = FiatCurrency.GetFromCode(currency!);
                                var sourceRateToUsd = _provider.GetFiatRateAt(currentDate, accountCurrency);
                                var targetRateFromUsd = _provider.GetFiatRateAt(currentDate, _currency);

                                if (sourceRateToUsd == 0 || targetRateFromUsd == 0)
                                    continue; // Skip if no rate available

                                var convertedBalance = targetRateFromUsd * (amount / sourceRateToUsd);
                                categoryFiatTotals[transaction.CategoryId] += convertedBalance;
                            }
                        }
                    }
                }
            }

            return new ExpensesByCategoryData()
            {
                MainCurrency = _currency,
                Items = categoryFiatTotals.Select(x => new ExpensesByCategoryData.Item()
                {
                    CategoryId = new CategoryId(x.Key.ToString()),
                    Icon = Icon.RestoreFromId(_provider.Categories[x.Key].Icon!),
                    CategoryName = BuildCategoryName(x.Key),
                    FiatTotal = x.Value
                }).OrderBy(x => x.CategoryName).ToList()
            };
        }

        private string BuildCategoryName(ObjectId id)
        {
            var category = _provider.Categories[id];

            return category.ParentId is not null ? $"{_provider.Categories[category.ParentId].Name} >> {category.Name}" : $"{category.Name}";
        }

        private static bool IsTransferTransaction(TransactionEntityType type)
        {
            return type is TransactionEntityType.FiatToFiat or
                         TransactionEntityType.BitcoinToBitcoin or
                         TransactionEntityType.FiatToBitcoin or
                         TransactionEntityType.BitcoinToFiat;
        }
    }
}
