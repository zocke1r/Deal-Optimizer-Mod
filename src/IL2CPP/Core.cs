using System.Text;
using HarmonyLib;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Economy;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Levelling;
using Il2CppScheduleOne.Messaging;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Persistence.Datas;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.UI.Handover;
using Il2CppScheduleOne.UI.Phone;
using Il2CppScheduleOne.UI.Phone.Messages;
using Il2CppScheduleOne.UI.Phone.ProductManagerApp;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using static Il2CppScheduleOne.UI.Handover.HandoverScreen;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(DealOptimizer_IL2CPP.Core), DealOptimizer_IL2CPP.BuildInfo.Name, DealOptimizer_IL2CPP.BuildInfo.Version, DealOptimizer_IL2CPP.BuildInfo.Author, null)]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace DealOptimizer_IL2CPP
{

    public static class BuildInfo
    {
        public const string Name = "HighBaller_IL2CPP";
        public const string Author = "zocke1r";
        public const string Version = "2.0.4";
    }

    public class Core : MelonMod
    {
        private static MelonPreferences_Category DealOptimizerCategory;
        private static MelonPreferences_Entry<float> MinSuccessProbability;
        private static MelonPreferences_Entry<bool> PriceOptimizationEnabled;

        private bool listening = false;

        private GUIStyle displayTextStyle;
        private static string counterOfferDisplayText = "";

        // Sales Info Panel
        private static bool showSalesInfo = false;
        private static ProductEntry selectedProduct = null;
        private static GUIStyle salesPanelStyle;
        private static GUIStyle salesHeaderStyle;
        private static GUIStyle salesTextStyle;
        private static Vector2 salesScrollPosition = Vector2.zero;
        private static string salesInfoText = "";

        private static float? lastCustomPrice = null;

        private static class DealCalculator
        {
            public static float CalculateSuccessProbability(Customer customer, ProductDefinition product, int quantity, float price)
            {
                var customerData = customer.CustomerData;

                float valueProposition = Customer.GetValueProposition(Registry.GetItem<ProductDefinition>(customer.OfferedContractInfo.Products.entries[0].ProductID),
                    customer.OfferedContractInfo.Payment / (float)customer.OfferedContractInfo.Products.entries[0].Quantity);
                float productEnjoyment = customer.GetProductEnjoyment(product, customerData.Standards.GetCorrespondingQuality());
                float enjoymentNormalized = Mathf.InverseLerp(-1f, 1f, productEnjoyment);
                float newValueProposition = Customer.GetValueProposition(product, price / (float)quantity);
                float quantityRatio = Mathf.Pow((float)quantity / (float)customer.OfferedContractInfo.Products.entries[0].Quantity, 0.6f);
                float quantityMultiplier = Mathf.Lerp(0f, 2f, quantityRatio * 0.5f);
                float penaltyMultiplier = Mathf.Lerp(1f, 0f, Mathf.Abs(quantityMultiplier - 1f));

                if (newValueProposition * penaltyMultiplier > valueProposition)
                {
                    return 1f;
                }
                if (newValueProposition < 0.12f)
                {
                    return 0f;
                }

                float customerWeightedValue = productEnjoyment * valueProposition;
                float proposedWeightedValue = enjoymentNormalized * penaltyMultiplier * newValueProposition;
                if (proposedWeightedValue > customerWeightedValue)
                {
                    return 1f;
                }

                float valueDifference = customerWeightedValue - proposedWeightedValue;
                float threshold = Mathf.Lerp(0f, 1f, valueDifference / 0.2f);
                float bonus = Mathf.Lerp(0f, 0.2f, Mathf.Max(customer.CurrentAddiction, customer.NPC.RelationData.NormalizedRelationDelta));

                float thresholdMinusBonus = threshold - bonus;
                return Mathf.Clamp01((0.9f - thresholdMinusBonus) / 0.9f);
            }

            public static (float maxSpend, float dailyAverage) CalculateSpendingLimits(Customer customer)
            {
                var customerData = customer.CustomerData;
                float adjustedWeeklySpend = customerData.GetAdjustedWeeklySpend(customer.NPC.RelationData.RelationDelta / 5f);
                var orderDays = customerData.GetOrderDays(customer.CurrentAddiction, customer.NPC.RelationData.RelationDelta / 5f);
                float dailyAverage = adjustedWeeklySpend / orderDays.Count;
                float maxSpend = dailyAverage * 3f;
                return (maxSpend, dailyAverage);
            }

            public static int FindOptimalPrice(Customer customer, ProductDefinition product, int quantity, float currentPrice, float maxSpend, float minSuccessProbability = -1f)
            {
                // Use preference value if not explicitly provided
                if (minSuccessProbability < 0)
                {
                    minSuccessProbability = MinSuccessProbability.Value;
                }

                int low = (int)currentPrice;
                int high = (int)maxSpend;
                int bestPossiblePrice = (int)currentPrice;
                int maxIterations = 20;
                int iterations = 0;

                Melon<Core>.Logger.Msg($"Binary Search Start - Price: {currentPrice}, MaxSpend: {maxSpend}, Quantity: {quantity}, MinProbability: {minSuccessProbability}, Low: {low}, High: {high}");

                while (iterations < maxIterations && low < high)
                {
                    int mid = (low + high) / 2;
                    float probability = CalculateSuccessProbability(customer, product, quantity, mid);
                    bool success = probability >= minSuccessProbability;

                    // Melon<Core>.Logger.Msg($"Binary Search Iteration {iterations}:");
                    // Melon<Core>.Logger.Msg($"  Testing price: {mid}");
                    // Melon<Core>.Logger.Msg($"  Success probability: {probability}");
                    // Melon<Core>.Logger.Msg($"  Success: {success}");
                    // Melon<Core>.Logger.Msg($"  Range: low={low}, high={high}");

                    if (success)
                    {
                        low = mid + 1;
                        if (low == high)
                        {
                            bestPossiblePrice = CalculateSuccessProbability(customer, product, quantity, mid + 1) > minSuccessProbability ? mid + 1 : mid;
                            break;
                        }
                        bestPossiblePrice = mid;
                    }
                    else
                    {
                        high = mid;
                    }
                    iterations++;
                }

                Melon<Core>.Logger.Msg($"Binary Search Complete:");
                Melon<Core>.Logger.Msg($"  Final bestFailingPrice: {bestPossiblePrice}");
                Melon<Core>.Logger.Msg($"  Final Probability: {CalculateSuccessProbability(customer, product, quantity, bestPossiblePrice)}");

                return bestPossiblePrice;
            }
        }
        public class PaymentQuantityResult
        {
            public float Payment { get; set; }
            public int Quantity { get; set; }
            public EQuality Quality { get; set; }
            public float Appeal { get; set; }

            public override string ToString()
            {
                return $"[Payment: {Payment}, Quantity: {Quantity}, Quality: {Quality}, Appeal: {Appeal}]";
            }
        }

        private static class CustomerHelper
        {
            public static Customer GetCustomerFromConversation(MSGConversation conversation)
            {
                string contactName = conversation.contactName;
                var unlockedCustomers = Customer.UnlockedCustomers;
                return unlockedCustomers.Find((Il2CppSystem.Predicate<Customer>)((cust) =>
                {
                    NPC npc = cust.NPC;
                    return npc.fullName == contactName;
                }));
            }

            public static Customer GetCustomerFromMessagesApp(MessagesApp messagesApp)
            {
                return GetCustomerFromConversation(messagesApp.currentConversation);
            }

            public static float GetProductAppeal(ProductDefinition product, Customer customer, float? overridePrice = null)
            {
                float productEnjoyment = customer.GetProductEnjoyment(product, customer.customerData.Standards.GetCorrespondingQuality());
                float num2 = (overridePrice ?? product.Price) / product.MarketValue;
                float num3 = Mathf.Lerp(1f, -1f, num2 / 2f);
                float value = productEnjoyment + num3;
                return value;
            }


            public static PaymentQuantityResult CalculatePaymentQuantity(ProductDefinition product, Customer customer, float? overridePrice = null)
            {
                var customerData = customer.customerData;
                // Early validation
                if (product == null)
                {
                    return null;
                }

                // Get customer standards quality
                EQuality correspondingQuality = customerData.Standards.GetCorrespondingQuality();

                // Use override price if provided, otherwise use product's base price
                float basePrice = overridePrice ?? product.Price;
                var appeal = GetProductAppeal(product, customer, overridePrice);
                if (appeal < 0.05f)
                {
                    return new PaymentQuantityResult
                    {
                        Payment = 0,
                        Quantity = 0,
                        Quality = correspondingQuality,
                        Appeal = appeal
                    };
                }

                // Calculate product enjoyment factor
                float productEnjoyment = customer.GetProductEnjoyment(product, correspondingQuality);

                // Calculate base spending adjusted for customer relationship
                int orderDaysCount = 7;
                if (customer.AssignedDealer == null)
                {
                    Il2CppSystem.Collections.Generic.List<EDay> orderDays = customerData.GetOrderDays(customer.CurrentAddiction, customer.NPC.RelationData.RelationDelta / 5f);
                    orderDaysCount = orderDays.Count;
                }

                // Calculate spending based on relationship and enjoyment
                float adjustedSpending = customerData.GetAdjustedWeeklySpend(customer.NPC.RelationData.RelationDelta / 5f) / (float)orderDaysCount;
                adjustedSpending *= Mathf.Lerp(0.66f, 1.5f, productEnjoyment);



                // Calculate price adjustment
                float adjustedPrice = basePrice * Mathf.Lerp(0.66f, 1.5f, productEnjoyment);

                // Calculate quantity
                int quantity = Mathf.RoundToInt(adjustedSpending / basePrice);
                quantity = Mathf.Clamp(quantity, 1, 1000);

                // Round quantity for larger orders
                if (quantity >= 14)
                {
                    quantity = Mathf.RoundToInt(quantity / 5) * 5;
                }

                // Calculate final payment (rounded to nearest 5)
                float payment = Mathf.RoundToInt(adjustedPrice * (float)quantity / 5f) * 5;

                return new PaymentQuantityResult
                {
                    Payment = payment,
                    Quantity = quantity,
                    Quality = correspondingQuality,
                    Appeal = appeal
                };
            }
        }

        private static class DisplayHelper
        {
            public static void UpdateCounterOfferDisplayText(string text, string reasonText)
            {
                counterOfferDisplayText = text + '\n' + reasonText;
            }
        }

        [HarmonyPatch(typeof(CounterofferInterface), nameof(CounterofferInterface.Open))]
        static class CounterofferInterfacePostOpenPatch
        {
            static void Postfix(ProductDefinition product, int quantity, float price, MSGConversation _conversation, Action<ProductDefinition, int, float> _orderConfirmedCallback)
            {
                MessagesApp messagesApp = PlayerSingleton<MessagesApp>.Instance;
                Customer customer = CustomerHelper.GetCustomerFromConversation(_conversation);

                var (maxSpend, _) = DealCalculator.CalculateSpendingLimits(customer);

                int optimalPrice = DealCalculator.FindOptimalPrice(customer, product, quantity, price, maxSpend);

                if (optimalPrice > price)
                {
                    messagesApp.CounterofferInterface.ChangePrice(optimalPrice - (int)price);
                }
            }
        }

        [HarmonyPatch(typeof(CounterofferInterface), nameof(CounterofferInterface.ChangePrice))]
        static class CounterofferInterfacePostChangePricePatch
        {
            static void Postfix(float change)
            {
                OfferPriceChangeCheckNoLog();
            }
        }

        [HarmonyPatch(typeof(CounterofferInterface), nameof(CounterofferInterface.ChangeQuantity))]
        static class CounterofferInterfacePostChangeQuantityPatch
        {
            static void Postfix(int change)
            {
                MessagesApp messagesApp = PlayerSingleton<MessagesApp>.Instance;
                if (messagesApp == null || !messagesApp.CounterofferInterface.IsOpen) return;

                Customer customer = CustomerHelper.GetCustomerFromMessagesApp(messagesApp);
                var (maxSpend, _) = DealCalculator.CalculateSpendingLimits(customer);

                CounterofferInterface counterofferInterface = messagesApp.CounterofferInterface;
                string quantityText = counterofferInterface.ProductLabel.text;
                int quantity = int.Parse(quantityText.Split("x ")[0]);
                string priceText = counterofferInterface.PriceInput.text;
                float currentPrice = priceText == "" ? 0 : float.Parse(priceText);

                ContractInfo contractOffer = customer.OfferedContractInfo;
                ProductDefinition product = Registry.GetItem<ProductDefinition>(contractOffer.Products.entries[0].ProductID);

                int optimalPrice = DealCalculator.FindOptimalPrice(customer, product, quantity, currentPrice * 0.25f, maxSpend);

                if (optimalPrice > currentPrice || change < 0)
                {
                    counterofferInterface.ChangePrice(optimalPrice - (int)currentPrice);
                }
            }
        }

        private static bool DefinitelyLessThan(float a, float b)
        {
            return (b - a) > ((Math.Abs(a) < Math.Abs(b) ? Math.Abs(b) : Math.Abs(a)) * 1E-15);
        }

        private class OfferData
        {
            public Customer Customer { get; }
            public ProductDefinition Product { get; }
            public int Quantity { get; }
            public float Price { get; }

            public OfferData(Customer customer, ProductDefinition product, int quantity, float price)
            {
                Customer = customer;
                Product = product;
                Quantity = quantity;
                Price = price;
            }
        }

        private static OfferData GetOfferData()
        {
            MessagesApp messagesApp = PlayerSingleton<MessagesApp>.Instance;
            Customer customer = CustomerHelper.GetCustomerFromMessagesApp(messagesApp);
            ContractInfo contractOffer = customer.OfferedContractInfo;
            ProductDefinition product = Registry.GetItem<ProductDefinition>(contractOffer.Products.entries[0].ProductID);

            string quantityText = messagesApp.CounterofferInterface.ProductLabel.text;
            int quantity = int.Parse(quantityText.Split("x ")[0]);

            string priceText = messagesApp.CounterofferInterface.PriceInput.text;
            float price = priceText == "" ? 0 : float.Parse(priceText);

            return new OfferData(customer, product, quantity, price);
        }

        public static void OfferPriceChangeCheckNoLog()
        {
            OfferData offerData = GetOfferData();
            EvaluateCounterOffer(offerData);
        }

        private void OfferPriceChangeCheckWithLog()
        {
            OfferData offerData = GetOfferData();
            StringBuilder stringBuilder = new StringBuilder();

            stringBuilder.Append('\n');
            stringBuilder.Append($"Customer Name: {offerData.Customer.name}\n");
            stringBuilder.Append($"Product: {offerData.Product.ID}\n");
            stringBuilder.Append($"Quantity: {offerData.Quantity}\n");
            stringBuilder.Append($"Price: {offerData.Price}\n");

            EvaluateCounterOffer(stringBuilder, offerData);
            LoggerInstance.Msg(stringBuilder.ToString());
        }

        private static bool EvaluateCounterOffer(OfferData offerData)
        {
            var (maxSpend, dailyAverage) = DealCalculator.CalculateSpendingLimits(offerData.Customer);
            decimal maxSpendDecimal = Math.Round((decimal)maxSpend, 2);

            if (offerData.Price >= maxSpend)
            {
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Failure", $"Exceeded Max Spend ({maxSpendDecimal})");
                return false;
            }

            float probability = DealCalculator.CalculateSuccessProbability(offerData.Customer, offerData.Product, offerData.Quantity, offerData.Price);
            decimal probabilityPercent = Math.Round((decimal)(probability * 100), 3);

            if (probability >= 0.95f)
            {
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Success", $"Price per unit: {offerData.Price / offerData.Quantity}\nMax Spend: {maxSpendDecimal}");
                return true;
            }
            else if (probability <= 0.05f)
            {
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Failure", $"Price per unit: {offerData.Price / offerData.Quantity}\nMax Spend: {maxSpendDecimal}");
                return false;
            }
            else
            {
                DisplayHelper.UpdateCounterOfferDisplayText($"Probability of success: {probabilityPercent}%", $"Price per unit: {offerData.Price / offerData.Quantity}\nMax Spend: {maxSpendDecimal}");
                return UnityEngine.Random.Range(0f, 1f) < probability;
            }
        }

        private bool EvaluateCounterOffer(StringBuilder stringBuilder, OfferData offerData)
        {
            var (maxSpend, dailyAverage) = DealCalculator.CalculateSpendingLimits(offerData.Customer);
            decimal maxSpendDecimal = Math.Round((decimal)maxSpend, 2);

            stringBuilder.Append('\n');
            stringBuilder.Append($"Adjusted Weekly Spend: {offerData.Customer.CustomerData.GetAdjustedWeeklySpend(offerData.Customer.NPC.RelationData.RelationDelta / 5f)}\n");
            stringBuilder.Append($"Order Days: {offerData.Customer.CustomerData.GetOrderDays(offerData.Customer.CurrentAddiction, offerData.Customer.NPC.RelationData.RelationDelta / 5f).Count}\n");
            stringBuilder.Append($"Average Daily Spend: {dailyAverage}\n");
            stringBuilder.Append($"Daily Spend Threshold (3x Avg): {maxSpendDecimal}\n");
            stringBuilder.Append($"Min Weekly Spend: {offerData.Customer.customerData.MinWeeklySpend}\n");
            stringBuilder.Append($"Max Weekly Spend: {offerData.Customer.customerData.MaxWeeklySpend}\n");
            var orderableProductString = "";
            foreach (var product in offerData.Customer.OrderableProducts)
            {
                orderableProductString += $"[Name: {product.Name}, Price: {product.Price}, Market: {product.MarketValue}],\n";
            }
            stringBuilder.Append($"Orderable Products: {orderableProductString}\n");

            if (offerData.Price >= maxSpend)
            {
                stringBuilder.Append("\nGuaranteed Failure - order must be less than 3x average daily spend\n");
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Failure", $"Exceeded Max Spend ({maxSpendDecimal})");
                return false;
            }

            float probability = DealCalculator.CalculateSuccessProbability(offerData.Customer, offerData.Product, offerData.Quantity, offerData.Price);
            decimal probabilityPercent = Math.Round((decimal)(probability * 100), 3);

            stringBuilder.Append('\n');
            stringBuilder.Append($"Success Probability: {probabilityPercent}%\n");

            if (probability >= 0.95f)
            {
                stringBuilder.Append("\nGuaranteed Success - high probability of success\n");
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Success", "");
                return true;
            }
            else if (probability <= 0.05f)
            {
                stringBuilder.Append("\nGuaranteed Failure - low probability of success\n");
                DisplayHelper.UpdateCounterOfferDisplayText("Guaranteed Failure", "");
                return false;
            }
            else
            {
                stringBuilder.Append($"\nProbability of success: {probabilityPercent}%\n");
                DisplayHelper.UpdateCounterOfferDisplayText($"Probability of success: {probabilityPercent}%", "");
                return UnityEngine.Random.Range(0f, 1f) < probability;
            }
        }

        [HarmonyPatch(typeof(HandoverScreen), nameof(HandoverScreen.Open))]
        static class HandoverScreenPostOpenPatch
        {
            static void Postfix(Contract contract, Customer customer, EMode mode, Action<EHandoverOutcome, List<ItemInstance>, float> callback, Func<List<ItemInstance>, float, float> successChanceMethod)
            {
                var (maxSpend, _) = DealCalculator.CalculateSpendingLimits(customer);
                HandoverScreen handoverScreen = Singleton<HandoverScreen>.Instance;

                handoverScreen.PriceSelector.SetPrice((int)maxSpend);

                float price = handoverScreen.PriceSelector.Price;
                if (!DefinitelyLessThan(price, maxSpend))
                {
                    handoverScreen.PriceSelector.SetPrice((int)maxSpend - 1);
                }
            }
        }

        public override void OnInitializeMelon()
        {
            displayTextStyle = new GUIStyle("label");
            displayTextStyle.fontSize = 18;
            displayTextStyle.normal.textColor = Color.black;
            displayTextStyle.normal.background = Texture2D.whiteTexture;
            displayTextStyle.alignment = TextAnchor.MiddleCenter;

            // Initialize sales panel styles
            salesPanelStyle = new GUIStyle("box");
            salesPanelStyle.normal.background = Texture2D.whiteTexture;
            salesPanelStyle.normal.textColor = Color.black;
            salesPanelStyle.padding = new RectOffset(10, 10, 10, 10);

            salesHeaderStyle = new GUIStyle("label");
            salesHeaderStyle.fontSize = 20;
            salesHeaderStyle.fontStyle = FontStyle.Bold;
            salesHeaderStyle.normal.textColor = Color.black;
            salesHeaderStyle.alignment = TextAnchor.MiddleCenter;

            salesTextStyle = new GUIStyle("label");
            salesTextStyle.fontSize = 14;
            salesTextStyle.normal.textColor = Color.black;
            salesTextStyle.wordWrap = true;

            // Initialize preferences
            DealOptimizerCategory = MelonPreferences.CreateCategory(BuildInfo.Name);
            MinSuccessProbability = DealOptimizerCategory.CreateEntry<float>("MinSuccessProbability", 0.98f, "Minimum Success Probability", "The minimum probability of success required for a deal (0.0 to 1.0)");
            PriceOptimizationEnabled = DealOptimizerCategory.CreateEntry<bool>("PriceOptimizationEnabled", false, "!!WIP!! Price Optimization Enabled", "Whether to enable price optimization");

            MinSuccessProbability.OnEntryValueChanged.Subscribe((_, _) => LoggerInstance.Msg($"Minimum success probability changed to: {MinSuccessProbability.Value}"));

            LoggerInstance.Msg("Initialized Mod");
        }
        public override void OnGUI()
        {
            bool gameStarted = SceneManager.GetActiveScene() != null && SceneManager.GetActiveScene().name == "Main";
            if (!gameStarted)
            {
                return;
            }

            Phone phone = PlayerSingleton<Phone>.Instance;
            bool phoneOpened = phone != null && phone.IsOpen;
            if (!phoneOpened)
            {
                return;
            }

            bool homeScreenOpened = PlayerSingleton<HomeScreen>.Instance.isOpen;
            bool counterofferInterfaceOpened = PlayerSingleton<MessagesApp>.Instance != null && PlayerSingleton<MessagesApp>.Instance.CounterofferInterface.IsOpen;

            if (!homeScreenOpened && counterofferInterfaceOpened)
            {
                GUI.Label(new Rect((Screen.width / 2) - 190, (Screen.height / 2) - 250, 380, 70), counterOfferDisplayText, displayTextStyle);
            }
            // Draw sales info panel if product manager is open and a product is selected
            if (productManagerApp != null && productManagerApp.isOpen && selectedProduct != null)
            {
                float panelWidth = 400f;
                float panelHeight = 500f;
                float panelX = Screen.width - panelWidth - 20f;
                float panelY = 20f;

                GUI.Box(new Rect(panelX, panelY, panelWidth, panelHeight), "", salesPanelStyle);

                // Header
                GUI.Label(new Rect(panelX, panelY, panelWidth, 40), "Sales Information", salesHeaderStyle);

                // Close button
                if (GUI.Button(new Rect(panelX + panelWidth - 30, panelY + 5, 25, 25), "X"))
                {
                    showSalesInfo = false;
                    selectedProduct = null;
                }

                // Scroll view for content
                salesScrollPosition = GUI.BeginScrollView(
                    new Rect(panelX, panelY + 40, panelWidth, panelHeight - 40),
                    salesScrollPosition,
                    new Rect(0, 0, panelWidth - 20, panelHeight - 60)
                );

                GUI.Label(new Rect(0, 0, panelWidth - 20, panelHeight - 60), salesInfoText, salesTextStyle);

                GUI.EndScrollView();
            }
        }

        public static ProductManager productManager;
        public static ProductManagerApp productManagerApp;

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName == "Main")
            {
                try
                {
                    productManager = Object.FindObjectsOfType<ProductManager>()[0];
                    Melon<Core>.Logger.Msg($"ProductManager: {productManager}");
                }
                catch (Exception e)
                {
                    Melon<Core>.Logger.Error($"Error finding ProductManager: {e.Message}");
                }
            }

            base.OnSceneWasLoaded(buildIndex, sceneName);
        }

        [HarmonyPatch(typeof(ProductManager), nameof(ProductManager.SendPrice))]
        static class ProductManagerSetPricePatch
        {
            static void Postfix(String productID, float value)
            {
                Melon<Core>.Logger.Msg($"ProductManager: Set price: {value} for {productID}");
                if (selectedProduct != null && selectedProduct.Definition.ID == productID)
                {
                    lastCustomPrice = value;
                    UpdateSalesInfoPanel(selectedProduct, value);
                }
            }
        }

        private static void UpdateSalesInfoPanel(ProductEntry entry, float? overridePrice = null)
        {
            selectedProduct = entry;
            showSalesInfo = true;
            salesScrollPosition = Vector2.zero;

            // Build sales info text
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"Product: {entry.Definition.Name}");
            sb.AppendLine($"Base Price: {entry.Definition.Price:C}");
            sb.AppendLine($"Market Value: {entry.Definition.MarketValue:C}");
            if (overridePrice.HasValue)
                sb.AppendLine($"Current Price: {overridePrice.Value:C}");
            sb.AppendLine();

            var customers = Customer.UnlockedCustomers;
            List<CustomerAppeal> customerAppeals = new List<CustomerAppeal>();
            for (int i = 0; i < customers.Count; i++)
            {
                customerAppeals.Add(new CustomerAppeal
                {
                    Customer = customers[i],
                    Appeal = CustomerHelper.GetProductAppeal(entry.Definition, customers[i], overridePrice),
                    PaymentQuantity = CustomerHelper.CalculatePaymentQuantity(entry.Definition, customers[i], overridePrice)
                });
            }

            var sortedAppeals = customerAppeals.OrderByDescending(ca => ca.Appeal).ToList();
            var potentialCustomers = sortedAppeals.FindAll(ca => ca.Appeal > 0.05f);

            sb.AppendLine($"Potential Customers: {potentialCustomers.Count}");
            sb.AppendLine($"Average Order Size: {(potentialCustomers.Count > 0 ? potentialCustomers.Average(ca => ca.PaymentQuantity.Quantity) : 0):F1}");
            sb.AppendLine($"Average Payment: {(potentialCustomers.Count > 0 ? potentialCustomers.Average(ca => ca.PaymentQuantity.Payment) : 0):C}");
            sb.AppendLine($"Average Per-Unit Price: {(potentialCustomers.Count > 0 ? potentialCustomers.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity) : 0):C}");
            sb.AppendLine($"Appeal: Customer: {(potentialCustomers.Count > 0 ? potentialCustomers.Average(ca => ca.Appeal) : 0):F2}, Min: {(potentialCustomers.Count > 0 ? potentialCustomers.Min(ca => ca.Appeal) : 0):F2}, Max: {(potentialCustomers.Count > 0 ? potentialCustomers.Max(ca => ca.Appeal) : 0):F2}");
            sb.AppendLine($"Appeal: Overall: {(sortedAppeals.Count > 0 ? sortedAppeals.Average(ca => ca.Appeal) : 0):F2}, Min: {(sortedAppeals.Count > 0 ? sortedAppeals.Min(ca => ca.Appeal) : 0):F2}, Max: {(sortedAppeals.Count > 0 ? sortedAppeals.Max(ca => ca.Appeal) : 0):F2}");
            sb.AppendLine();

            if (PriceOptimizationEnabled.Value)
            {
                // Find optimal price
                float initialPrice = overridePrice ?? entry.Definition.Price;
                float minPrice = initialPrice * 0.5f;
                float maxPrice = initialPrice * 2f;
                var (bestPrice, bestPerUnitPrice) = FindOptimalPrice(entry.Definition, sortedAppeals, initialPrice, minPrice, maxPrice);

                sb.AppendLine("Price Optimization Results:");
                sb.AppendLine($"  Original Price: {entry.Definition.Price:C}");
                sb.AppendLine($"  Optimal Price: {bestPrice:C}");
                sb.AppendLine($"  Price Change: {((bestPrice - entry.Definition.Price) / entry.Definition.Price * 100):F1}%");
                sb.AppendLine($"  Original Per-Unit Price: {(potentialCustomers.Count > 0 ? potentialCustomers.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity) : 0):C}");
                sb.AppendLine($"  Optimal Per-Unit Price: {bestPerUnitPrice:C}");
                sb.AppendLine($"  Per-Unit Price Change: {(potentialCustomers.Count > 0 && potentialCustomers.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity) != 0 ? ((bestPerUnitPrice - potentialCustomers.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity)) / potentialCustomers.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity) * 100) : 0):F1}%");
            }

            salesInfoText = sb.ToString();
        }

        [HarmonyPatch(typeof(ProductManagerApp), nameof(ProductManagerApp.SelectProduct))]
        static class ProductManagerSelectProductPatch
        {
            public static void Prefix(ProductManagerApp __instance, ProductEntry entry)
            {
                productManagerApp = __instance;
            }
            static void Postfix(ProductEntry entry)
            {
                lastCustomPrice = null;
                UpdateSalesInfoPanel(entry);
            }
        }

        private class CustomerAppeal
        {
            public Customer Customer { get; set; }
            public float Appeal { get; set; }
            public PaymentQuantityResult PaymentQuantity { get; set; }
        }

        private static (float bestPrice, float bestPerUnitPrice) FindOptimalPrice(ProductDefinition product, List<CustomerAppeal> customerAppeals, float initialPrice, float minPrice, float maxPrice, int maxIterations = 20)
        {
            int low = Mathf.FloorToInt(minPrice);
            int high = Mathf.CeilToInt(maxPrice);
            int bestPrice = Mathf.RoundToInt(initialPrice);
            float bestPerUnitPrice = 0f;
            int iterations = 0;

            Melon<Core>.Logger.Msg($"Starting ternary search for optimal per-unit price");
            Melon<Core>.Logger.Msg($"Initial price: {initialPrice}");
            Melon<Core>.Logger.Msg($"Search range: {low} to {high}");

            while (iterations < maxIterations && high - low > 1)
            {
                int leftThird = low + (high - low) / 3;
                int rightThird = high - (high - low) / 3;

                // Calculate per-unit price for left third price
                var leftAppeals = customerAppeals.ToList();
                leftAppeals.ForEach(ca => ca.PaymentQuantity = CustomerHelper.CalculatePaymentQuantity(product, ca.Customer, leftThird));
                float leftPerUnitPrice = leftAppeals.Average(ca => ca.PaymentQuantity.Quantity == 0 ? 0 : ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity);

                // Calculate per-unit price for right third price
                var rightAppeals = customerAppeals.ToList();
                rightAppeals.ForEach(ca => ca.PaymentQuantity = CustomerHelper.CalculatePaymentQuantity(product, ca.Customer, rightThird));
                float rightPerUnitPrice = rightAppeals.Average(ca => ca.PaymentQuantity.Quantity == 0 ? 0 : ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity);

                Melon<Core>.Logger.Msg($"Iteration {iterations}:");
                Melon<Core>.Logger.Msg($"  Left third ({leftThird}): {leftPerUnitPrice:F2} per unit");
                Melon<Core>.Logger.Msg($"  Right third ({rightThird}): {rightPerUnitPrice:F2} per unit");

                // Update best price if we found a better one
                if (leftPerUnitPrice > bestPerUnitPrice)
                {
                    bestPrice = leftThird;
                    bestPerUnitPrice = leftPerUnitPrice;
                }
                if (rightPerUnitPrice > bestPerUnitPrice)
                {
                    bestPrice = rightThird;
                    bestPerUnitPrice = rightPerUnitPrice;
                }

                // Narrow the search range
                if (leftPerUnitPrice > rightPerUnitPrice)
                {
                    high = rightThird;
                }
                else
                {
                    low = leftThird;
                }

                iterations++;
            }

            // Final check of remaining prices
            for (int price = low; price <= high; price++)
            {
                var currentAppeals = customerAppeals.ToList();
                currentAppeals.ForEach(ca => ca.PaymentQuantity = CustomerHelper.CalculatePaymentQuantity(product, ca.Customer, price));
                float currentPerUnitPrice = currentAppeals.Average(ca => ca.PaymentQuantity.Payment / ca.PaymentQuantity.Quantity);

                if (currentPerUnitPrice > bestPerUnitPrice)
                {
                    bestPrice = price;
                    bestPerUnitPrice = currentPerUnitPrice;
                }
            }

            Melon<Core>.Logger.Msg($"Ternary search complete after {iterations} iterations");
            Melon<Core>.Logger.Msg($"Best price found: {bestPrice}");
            Melon<Core>.Logger.Msg($"Best per-unit price: {bestPerUnitPrice:F2}");

            return (bestPrice, bestPerUnitPrice);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (listening)
            {
                listening = false;
            }
        }

        public override void OnUpdate()
        {
            bool gameStarted = SceneManager.GetActiveScene() != null && SceneManager.GetActiveScene().name == "Main";
            MessagesApp messagesAppInstance = PlayerSingleton<MessagesApp>.Instance;

            if (gameStarted && !listening && messagesAppInstance != null)
            {
                Subscribe();
            }
        }

        private UnityAction Subscribe()
        {
            UnityAction<string> changeListener = (UnityAction<string>)((string unused) => OfferPriceChangeCheckWithLog());

            MessagesApp messagesAppInstance = PlayerSingleton<MessagesApp>.Instance;
            messagesAppInstance.CounterofferInterface.PriceInput.onValueChanged.AddListener(changeListener);

            LoggerInstance.Msg("Attached listener");
            listening = true;

            return (UnityAction)(() => PlayerSingleton<MessagesApp>.Instance.CounterofferInterface.PriceInput.onValueChanged.RemoveListener(changeListener));
        }
    }
}