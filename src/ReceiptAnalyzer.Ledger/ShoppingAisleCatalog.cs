namespace ReceiptAnalyzer.Ledger;

/// <summary>
/// Assigns ordinary UK supermarket products to a stable walking order for generated shopping lists.
/// The catalogue is deliberately deterministic: list creation must be instant and must also work
/// when the installed PWA is offline.
/// </summary>
public static class ShoppingAisleCatalog
{
    public const string FreshProduce = "Fresh produce";
    public const string Bakery = "Bakery";
    public const string DairyAndEggs = "Milk, dairy & eggs";
    public const string MeatAndFish = "Meat & fish";
    public const string Chilled = "Chilled";
    public const string Frozen = "Frozen";
    public const string TinsAndJars = "Tins & jars";
    public const string DryGroceries = "Dry groceries";
    public const string SoftDrinks = "Soft drinks";
    public const string CleaningAndHousehold = "Cleaning & household";
    public const string Toiletries = "Toiletries";
    public const string ChocolateAndSnacks = "Chocolate, sweets & snacks";
    public const string Alcohol = "Alcohol";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> Ordered =
    [
        FreshProduce, Bakery, DairyAndEggs, MeatAndFish, Chilled, Frozen,
        TinsAndJars, DryGroceries, SoftDrinks, CleaningAndHousehold, Toiletries,
        ChocolateAndSnacks, Alcohol, Other
    ];

    private static readonly IReadOnlyDictionary<string, int> Ranks = Ordered
        .Select((aisle, index) => (aisle, index))
        .ToDictionary(x => x.aisle, x => x.index, StringComparer.OrdinalIgnoreCase);

    public static int SortOrder(string aisle) => Ranks.GetValueOrDefault(aisle, Ordered.Count - 1);

    public static string Classify(string item)
    {
        var words = " " + KeyNormaliser.Normalise(item).Replace('-', ' ') + " ";

        // Product-name traps are checked before broad terms such as milk, beer and wine.
        if (ContainsAny(words, "wine gums", "beer nuts", "milk chocolate", "chocolate milk"))
            return ChocolateAndSnacks;
        if (ContainsAny(words, "ginger beer", "ginger ale", "root beer", "alcohol free", "non alcoholic", "zero alcohol"))
            return SoftDrinks;
        if (ContainsAny(words, "wine vinegar", "cider vinegar"))
            return DryGroceries;

        // Preparation/storage words outrank ingredients: frozen peas belong by the freezers,
        // tinned tomatoes by tins, and cream cleaner with household products.
        if (ContainsAny(words, "frozen", "ice cream", "ice lolly", "ice lollies"))
            return Frozen;
        if (ContainsAny(words, "green beans", "runner beans", "fine beans"))
            return FreshProduce;
        if (ContainsAny(words, "tinned", "canned", "tin of", "can of", "beans", "chickpeas", "passata", "pasta sauce", "cooking sauce", "jar", "pickles"))
            return TinsAndJars;
        if (ContainsAny(words, "washing up", "laundry", "detergent", "fabric conditioner", "dishwasher", "bleach", "cleaner", "cleaning", "toilet roll", "kitchen roll", "bin bags", "foil", "cling film", "sponges"))
            return CleaningAndHousehold;
        if (ContainsAny(words, "chocolate", "sweets", "sweet", "crisps", "snack", "biscuits", "biscuit", "cookies", "cookie", "popcorn", "nuts", "cake", "cakes"))
            return ChocolateAndSnacks;
        if (ContainsAny(words, "ready meal", "hummus", "houmous", "coleslaw", "fresh pasta", "sandwich", "quiche", "chilled"))
            return Chilled;
        if (ContainsAny(words, "wine", "beer", "lager", "ale", "cider", "vodka", "gin", "rum", "whisky", "whiskey", "prosecco", "champagne", "liqueur"))
            return Alcohol;
        if (ContainsAny(words, "cola", "lemonade", "squash", "juice", "smoothie", "tonic", "sparkling water", "soft drink"))
            return SoftDrinks;

        if (ContainsAny(words,
            "apple", "apples", "banana", "bananas", "orange", "oranges", "lemon", "lemons", "lime", "limes",
            "grape", "grapes", "pear", "pears", "berry", "berries", "strawberry", "strawberries", "blueberry",
            "raspberry", "raspberries", "blueberries", "melon", "watermelon", "mango", "pineapple", "avocado", "kiwi",
            "plum", "plums", "peach", "peaches", "nectarine", "nectarines", "satsuma", "satsumas",
            "clementine", "clementines", "mandarin", "mandarins", "grapefruit", "tomato", "tomatoes",
            "potato", "potatoes", "onion", "onions", "carrot", "carrots", "broccoli", "cauliflower", "cabbage",
            "lettuce", "spinach", "cucumber", "pepper", "peppers", "courgette", "courgettes", "aubergine", "aubergines",
            "mushroom", "mushrooms", "peas", "sweetcorn", "celery", "leek", "leeks", "garlic", "ginger", "beetroot",
            "parsnip", "parsnips", "swede", "kale", "rocket", "radish", "radishes", "asparagus", "chilli", "chillies",
            "salad", "fresh herbs", "coriander", "parsley", "basil"))
            return FreshProduce;

        if (ContainsAny(words, "bread", "loaf", "rolls", "bagel", "bagels", "wraps", "pitta", "naan", "croissant", "brioche", "muffin", "muffins", "bakery", "crumpet", "crumpets"))
            return Bakery;
        if (ContainsAny(words, "milk", "cheese", "cheddar", "yoghurt", "yogurt", "butter", "cream", "eggs", "egg", "kefir", "skyr"))
            return DairyAndEggs;
        if (ContainsAny(words, "chicken", "beef", "pork", "lamb", "turkey", "bacon", "ham", "sausage", "sausages", "steak", "mince", "salmon", "tuna steak", "cod", "haddock", "mackerel", "trout", "sea bass", "prawn", "prawns", "crab", "fish"))
            return MeatAndFish;
        if (ContainsAny(words, "pasta", "rice", "cereal", "porridge", "oats", "flour", "sugar", "coffee", "tea", "soup", "stock", "spice", "herbs", "noodles", "oil", "vinegar", "marmite", "jam", "peanut butter", "ketchup", "mayonnaise", "mustard", "condiment"))
            return DryGroceries;
        if (ContainsAny(words, "shampoo", "conditioner", "shower gel", "soap", "toothpaste", "toothbrush", "deodorant", "razor", "tissues", "toiletries"))
            return Toiletries;

        return Other;
    }

    private static bool ContainsAny(string words, params string[] terms) =>
        terms.Any(term => words.Contains(" " + term + " ", StringComparison.Ordinal));
}
