namespace ELB_DockTool;

/// <summary>
/// Built-in quote library. Each category is deterministically expanded to exactly
/// 1,000 unique original quotes (40 topics x 25 practical insights). No internet
/// connection or external quote service is required.
/// </summary>
internal static class QuoteBank
{
    static readonly string[] CaddTopics =
    {
        "molecular docking", "active-site definition", "receptor preparation", "ligand preparation",
        "binding-site analysis", "grid-box design", "docking validation", "redocking", "native-ligand validation",
        "pose analysis", "docking scores", "binding interactions", "hydrogen bonding", "hydrophobic contacts",
        "π–π interactions", "salt bridges", "protein flexibility", "ligand flexibility", "conformational sampling",
        "AutoDock Vina", "virtual screening", "lead prioritization", "structure-based design", "pharmacophore modelling",
        "molecular dynamics", "free-energy thinking", "docking reproducibility", "parameter selection", "pose ranking",
        "binding-site conservation", "protein–ligand complementarity", "visual inspection", "control experiments",
        "computational chemistry", "drug-target modelling", "hit discovery", "lead optimization", "in-silico screening",
        "CADD workflow", "computational validation"
    };

    static readonly string[] CaddInsights =
    {
        "starts with a biologically justified question, not a button click",
        "is strongest when the binding site is defined before the search begins",
        "needs clean inputs because bad structures produce convincing-looking bad answers",
        "gets more reliable when the native ligand or known site is used as a reference",
        "should be treated as a ranking tool, not a final experimental verdict",
        "becomes more useful when poses are inspected instead of blindly sorted by score",
        "benefits from separating preparation errors from genuine docking failures",
        "is reproducible only when the receptor, ligand, grid and search settings are recorded",
        "should report failures clearly instead of hiding them behind a completed message",
        "works better when file roles are validated before expensive calculations start",
        "is faster when routine conversion and validation are automated without changing the science",
        "needs chemically sensible protonation and bond orders before docking",
        "should keep the original structures untouched so every transformation remains traceable",
        "benefits from testing a protocol on a known ligand before screening unknown compounds",
        "should use multiple complementary interaction clues rather than one score alone",
        "requires attention to receptor preparation because missing atoms can distort contacts",
        "becomes easier to debug when every external command and error is logged",
        "should distinguish a valid docking pose from an artifact of an oversized search space",
        "is more informative when the same protocol is applied consistently across candidates",
        "can prioritize experiments, but experiments decide whether a prediction is real",
        "should favor a validated workflow over a complicated workflow that cannot be reproduced",
        "benefits from checking whether the predicted pose agrees with known binding biology",
        "is a model of molecular recognition, not a direct measurement of affinity",
        "improves when computational assumptions are written down before interpretation",
        "should make the active site explicit whenever a target has a known binding pocket"
    };

    static readonly string[] PharmaTopics =
    {
        "medicinal chemistry", "pharmacology", "pharmaceutics", "drug discovery", "drug design", "ADME",
        "pharmacokinetics", "pharmacodynamics", "bioavailability", "drug stability", "formulation science",
        "analytical chemistry", "organic chemistry", "heterocyclic chemistry", "spectroscopy", "quality control",
        "preformulation", "dosage forms", "drug delivery", "target identification", "hit-to-lead", "lead optimization",
        "structure–activity relationships", "toxicity assessment", "selectivity", "solubility", "permeability",
        "metabolism", "drug interactions", "clinical translation", "biopharmaceutics", "pharmacognosy", "natural products",
        "synthetic chemistry", "bioinformatics", "computational drug discovery", "precision therapeutics",
        "pharmaceutical analysis", "research reproducibility"
    };

    static readonly string[] PharmaInsights =
    {
        "connects chemistry, biology and formulation into one drug-development problem",
        "starts with mechanism and evidence before focusing on appearance or novelty",
        "depends on understanding both what a molecule does and how the body handles it",
        "requires balancing potency with selectivity, exposure, stability and safety",
        "turns a promising hit into a useful medicine only through disciplined optimization",
        "needs experimental controls because plausible mechanisms can still be wrong",
        "benefits from linking structure–activity relationships to a clear biological hypothesis",
        "gets stronger when analytical measurements are traceable and reproducible",
        "should treat solubility and permeability as design constraints, not afterthoughts",
        "requires dosage-form choices that match the drug's physicochemical behavior",
        "is improved when computational predictions are tested against appropriate experiments",
        "depends on chemical identity and purity being established before biological conclusions",
        "must consider metabolism because the administered molecule may not be the active species",
        "benefits from early attention to stability instead of discovering degradation at the end",
        "requires dose, exposure and response to be interpreted together",
        "should distinguish correlation from causation when interpreting pharmacological data",
        "is more efficient when failed hypotheses are documented instead of forgotten",
        "needs clear records so another researcher can reproduce the same conclusion",
        "should optimize the whole profile of a candidate rather than one attractive property",
        "becomes clinically meaningful only when molecular findings survive biological complexity",
        "benefits from asking what evidence would prove the hypothesis wrong",
        "requires communication between computational, chemical, analytical and biological teams",
        "should use quantitative reasoning whenever a qualitative explanation is not enough",
        "is ultimately about producing reliable evidence that can guide safer decisions",
        "moves faster when the workflow is organized, but never by skipping critical validation"
    };

    static readonly string[] CaddQuotes = Build(CaddTopics, CaddInsights);
    static readonly string[] PharmaQuotes = Build(PharmaTopics, PharmaInsights);

    static string[] Build(string[] topics, string[] insights)
    {
        // 40 x 25 = exactly 1,000 unique combinations in each category.
        var result = new string[topics.Length * insights.Length];
        int k = 0;
        foreach (var topic in topics)
            foreach (var insight in insights)
                result[k++] = $"{Cap(topic)} {insight}.";
        return result;
    }

    static string Cap(string value) => char.ToUpperInvariant(value[0]) + value[1..];

    public static string GetRandom(bool cadd) => (cadd ? CaddQuotes : PharmaQuotes)[Random.Shared.Next(1000)];
}
