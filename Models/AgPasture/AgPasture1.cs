﻿using System;
using System.Reflection;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Linq.Expressions;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using Models;
using Models.Core;
using Models.Soils;

namespace Models.AgPasture1
{

    /// <summary>
    /// A multi-mySpecies pasture model 
    /// </summary>
    [Serializable]
    [ViewName("UserInterface.Views.GridView")]
    [PresenterName("UserInterface.Presenters.PropertyPresenter")]
    public class Sward : Model, ICrop
    {
        #region Links, events and delegates  -------------------------------------------------------------------------------

        [Link]
        private Clock clock = null;

        [Link]
        private Soils.Soil Soil = null;

        [Link]
        private WeatherFile MetData = null;

        [Link]
        private ISummary Summary = null;

        //Events
        public delegate void NewCropDelegate(PMF.NewCropType Data);
        public event NewCropDelegate NewCrop;

        public event EventHandler Sowing;

        public delegate void NewCanopyDelegate(NewCanopyType Data);
        public event NewCanopyDelegate NewCanopy;

        public delegate void FOMLayerDelegate(Soils.FOMLayerType Data);
        public event FOMLayerDelegate IncorpFOM;

        public delegate void BiomassRemovedDelegate(PMF.BiomassRemovedType Data);
        public event BiomassRemovedDelegate BiomassRemoved;

        public delegate void WaterChangedDelegate(PMF.WaterChangedType Data);
        public event WaterChangedDelegate WaterChanged;

        public delegate void NitrogenChangedDelegate(Soils.NitrogenChangedType Data);
        public event NitrogenChangedDelegate NitrogenChanged;

        #endregion

        #region ICrop implementation  --------------------------------------------------------------------------------------

        /// <summary>
        /// Generic decriptor used by MicroClimate to look up for canopy properties for this plant
        /// </summary>
        [Description("Generic type of crop")]
        [Units("")]
        public string CropType
        {
            get { return Name; }
        }

        /// <summary>
        /// Gets a list of cultivar names (not used by AgPasture)
        /// </summary>
        public string[] CultivarNames
        {
            get { return null; }
        }

        /// <summary>
        /// Potential evapotranspiration, as calculated by MicroClimate
        /// </summary>
        [XmlIgnore]
        public double PotentialEP
        {
            get { return swardWaterDemand; }
            set
            {
                swardWaterDemand = value;

                // partition the demand among species
                if (isSwardControlled)
                    foreach (PastureSpecies mySpecies in mySward)
                        mySpecies.myWaterDemand = swardWaterDemand * mySpecies.AboveGrounLivedWt / AboveGroundLiveWt;
            }
        }

        private double interceptedRadn;
        private CanopyEnergyBalanceInterceptionlayerType[] myLightProfile;
        /// <summary>
        /// Energy available for each canopy layer, as calcualted by MicroClimate
        /// </summary>
        [XmlIgnore]
        public CanopyEnergyBalanceInterceptionlayerType[] LightProfile
        {
            get { return myLightProfile; }
            set
            {
                interceptedRadn = 0.0;
                for (int s = 0; s < value.Length; s++)
                {
                    myLightProfile = value;
                    interceptedRadn += myLightProfile[s].amount;

                    // pass on the radiation to each species  -  shouldn't this be partitioned???
                    if (isSwardControlled)
                        foreach (PastureSpecies mySpecies in mySward)
                            mySpecies.interceptedRadn = interceptedRadn;
                }
            }
        }

        /// <summary>
        /// Data about this plant's canopy (LAI, height, etc), used by MicroClimate
        /// </summary>
        private NewCanopyType myCanopyData = new NewCanopyType();

        /// <summary>
        /// Data about this plant's canopy (LAI, height, etc), used by MicroClimate
        /// </summary>
        public NewCanopyType CanopyData
        {
            get { return myCanopyData; }
        }

        private Soils.RootSystem rootSystem;
        /// <summary>
        /// Root system information for this crop
        /// </summary>
        [XmlIgnore]
        public Soils.RootSystem RootSystem
        {
            get { return rootSystem; }
            set { rootSystem = value; }
        }

        /// <summary>
        /// Plant growth limiting factor for other module calculating potential transpiration
        /// </summary>
        public double FRGR
        {
            get
            {
                double Tday = 0.75 * MetData.MaxT + 0.25 * MetData.MinT;
                double gft;
                if (Tday < 20)
                    gft = Math.Sqrt(GLFtemp);
                else 
                    gft = GLFtemp;
                // Note: p_gftemp is for gross photosysthsis.
                // This is different from that for net production as used in other APSIM crop models, and is
                // assumesd in calculation of temperature effect on transpiration (in micromet).
                // Here we passed it as sqrt - (Doing so by a comparison of p_gftemp and that
                // used in wheat). Temperature effects on NET produciton of forage mySpecies in other models
                // (e.g., grassgro) are not so significant for T = 10-20 degrees(C)

                //Also, have tested the consequences of passing p_Ncfactor in (different concept for gfwater),
                //coulnd't see any differnece for results
                return Math.Min(FVPD, gft);
                // RCichota, Jan/2014: removed AgPasture's Frgr from here, it is considered at the same level as nitrogen etc...
            }
        }

        // TODO: Have to verify how this works, it seems Microclime needs a sow event, not new crop...
        /// <summary>
        /// Event publication - new crop
        /// </summary>
        private void DoNewCropEvent()
        {
            if (NewCrop != null)
            {
                // Send out New Crop Event to tell other modules who I am and what I am
                PMF.NewCropType EventData = new PMF.NewCropType();
                EventData.crop_type = "Pasture";
                EventData.sender = Name;
                NewCrop.Invoke(EventData);
            }

            if (Sowing != null)
                Sowing.Invoke(this, new EventArgs());

        }

        #endregion

        #region Model parameters  ------------------------------------------------------------------------------------------

        // = General parameters  ==================================================================

        // [Link]
        //mySpecies[] mySpecies;
        [XmlIgnore]
        public PastureSpecies[] mySward { get; private set; }

        private int numSpecies = 1;
        //[Description("Number of mySpecies")] 
        [XmlIgnore]
        public int NumSpecies
        {
            get { return numSpecies; }
            set { numSpecies = value; }
        }

       // * Parameters that are set via user interface -------------------------------------------

        private bool isSwardControlled = true;
        [Description("Test - Is sward controlling the processes in all pasture species?")]
        public string AgPastureControlled
        {
            get {
                if (isSwardControlled)
                    return "yes";
                else
                    return "no";
            }
            set { isSwardControlled = (value.ToLower()=="yes"); }
        }

        private string waterUptakeSource = "Sward";
        [Description("Which model is responsible for water uptake ('sward', pasture 'species', or 'apsim')?")]
        public string WaterUptakeSource
        {
            get { return waterUptakeSource; }
            set { waterUptakeSource = value; }
        }

        private string nUptakeSource = "Sward";
        [Description("Which model is responsible for nitrogen uptake ('sward', pasture 'species', or 'apsim')?")]
        public string NUptakeSource
        {
            get { return nUptakeSource; }
            set { nUptakeSource = value; }
        }

        private string useAltWUptake = "no";
        [Description("Test - Use alternative water uptake process")]
        public string UseAlternativeWaterUptake
        {
            get { return useAltWUptake; }
            set { useAltWUptake = value; }
        }

        private string useAltNUptake = "no";
        [Description("Test - Use alternative N uptake process")]
        public string UseAlternativeNUptake
        {
            get { return useAltNUptake; }
            set { useAltNUptake = value; }
        }

        private double referenceKSuptake = 1000.0;
        [Description("Test - Reference soil Ksat for optimum water uptake (if using alternative method)")]
        public double ReferenceKSuptake
        {
            get { return referenceKSuptake; }
            set { referenceKSuptake = value; }
        }

        //private double[] preferenceForGreenDM = new double[] { 1.0, 1.0, 1.0 };
        //[XmlIgnore]
        //public double[] PreferenceForGreenDM
        //{
        //    get { return preferenceForGreenDM; }
        //    set
        //    {
        //        int NSp = value.Length;
        //        preferenceForGreenDM = new double[NSp];
        //        for (int sp = 0; sp < NSp; sp++)
        //            preferenceForGreenDM[sp] = value[sp];
        //    }
        //}

        //private double[] preferenceForDeadDM = new double[] { 1.0, 1.0, 1.0 };
        //[XmlIgnore]
        //public double[] PreferenceForDeadDM
        //{
        //    get { return preferenceForDeadDM; }
        //    set
        //    {
        //        int NSp = value.Length;
        //        preferenceForDeadDM = new double[NSp];
        //        for (int sp = 0; sp < NSp; sp++)
        //            preferenceForDeadDM[sp] = value[sp];
        //    }
        //}

        // * Other parameters (changed via manager) -----------------------------------------------

        private string rootDistributionMethod = "ExpoLinear";
        //[Description("Root distribution method")]
        [XmlIgnore]
        public string RootDistributionMethod
        {
            get
            { return rootDistributionMethod; }
            set
            {
                switch (value.ToLower())
                {
                    case "homogenous":
                    case "userdefined":
                    case "expolinear":
                        rootDistributionMethod = value;
                        break;
                    default:
                        throw new Exception("No valid method for computing root distribution was selected");
                }
            }
        }

        private double expoLinearDepthParam = 0.1;
        [Description("Fraction of root depth where its proportion starts to decrease")]
        public double ExpoLinearDepthParam
        {
            get { return expoLinearDepthParam; }
            set
            {
                expoLinearDepthParam = value;
                if (expoLinearDepthParam == 1.0)
                    rootDistributionMethod = "Homogeneous";
            }
        }

        private double expoLinearCurveParam = 3.0;
        [Description("Exponent to determine mass distribution in the soil profile")]
        public double ExpoLinearCurveParam
        {
            get { return expoLinearCurveParam; }
            set
            {
                expoLinearCurveParam = value;
                if (expoLinearCurveParam == 0.0)
                    rootDistributionMethod = "Homogeneous";	// It is impossible to solve, but its limit is a homogeneous distribution
            }
        }

        /// <summary>
        /// Broken stick type function describing how plant height varies with DM
        /// </summary>
        [XmlIgnore]
        public BrokenStick HeightFromMass = new BrokenStick
        {
            X = new double[5] { 0, 1000, 2000, 3000, 4000 },
            Y = new double[5] { 0, 25, 75, 150, 250 }
        };

        #endregion

        #region Model outputs  ---------------------------------------------------------------------------------------------

        [Description("Plant status (dead, alive, etc)")]
        [Units("")]
        public string PlantStatus
        {
            get
            {
                if (mySward.Any(mySpecies => mySpecies.PlantStatus == "alive"))
                    return "alive";
                else
                    return "out";
            }
        }

        [Description("Plant development stage number, approximate")]
        [Units("")]
        public int Stage
        {
            // An approximate of the stage number, corresponding to that of other arable crops. For management applications.
            // Phenostage of the first mySpecies (ryegrass) is used for this approximation
            get
            {
                if (PlantStatus == "alive")
                {
                    if (mySward.Any(mySpecies => mySpecies.Stage == 3))
                        return 3;    // 'emergence'
                    else
                        return 1;    //'sowing or germination';
                }
                else
                    return 0;
            }
        }

        [Description("Plant development stage name")]
        [Units("")]
        public string StageName
        {
            get
            {
                if (PlantStatus == "alive")
                {
                    if (Stage == 1)
                        return "sowing";
                    else
                        return "emergence";
                }
                return "out";
            }
        }

        #region - DM and C amounts  ----------------------------------------------------------------------------------------

        [Description("Total amount of C in plants")]
        [Units("kgDM/ha")]
        public double TotalPlantC
        {
            get { return mySward.Sum(mySpecies => mySpecies.TotalWt * CinDM); }
        }

        [Description("Total dry matter weight of plants")]
        [Units("kgDM/ha")]
        public double TotalPlantWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.TotalWt); }
        }

        [Description("Total dry matter weight of plants above ground")]
        [Units("kgDM/ha")]
        public double AboveGroundPlantWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGroundWt); }
        }

        [Description("Total dry matter weight of plants alive above ground")]
        [Units("kgDM/ha")]
        public double AboveGroundLiveWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGrounLivedWt); }
        }

        [Description("Total dry matter weight of dead plants above ground")]
        [Units("kgDM/ha")]
        public double AboveGroundDeadWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGroundDeadWt); }
        }

        [Description("Total dry matter weight of plants below ground")]
        [Units("kgDM/ha")]
        public double BelowGroundWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.RootWt); }
        }

        [Description("Total dry matter weight of standing plants parts")]
        [Units("kgDM/ha")]
        public double StandingPlantWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingWt); }
        }

        [Description("Dry matter weight of live standing plants parts")]
        [Units("kgDM/ha")]
        public double StandingLiveWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingLiveWt); }
        }

        [Description("Dry matter weight of dead standing plants parts")]
        [Units("kgDM/ha")]
        public double StandingDeadWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingDeadWt); }
        }

        [Description("Total dry matter weight of plant's leaves")]
        [Units("kgDM/ha")]
        public double LeafWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.LeafWt); }
        }

        [Description("Total dry matter weight of plant's leaves alive")]
        [Units("kgDM/ha")]
        public double LeafLiveWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.LeafGreenWt); }
        }

        [Description("Total dry matter weight of plant's leaves dead")]
        [Units("kgDM/ha")]
        public double LeafDeadWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.LeafDeadWt); }
        }

        [Description("Total dry matter weight of plant's stems")]
        [Units("kgDM/ha")]
        public double StemWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StemWt); }
        }

        [Description("Total dry matter weight of plant's stems alive")]
        [Units("kgDM/ha")]
        public double StemLiveWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StemGreenWt); }
        }

        [Description("Total dry matter weight of plant's stems dead")]
        [Units("kgDM/ha")]
        public double StemDeadWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StemDeadWt); }
        }

        [Description("Total dry matter weight of plant's stolons")]
        [Units("kgDM/ha")]
        public double StolonWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.StolonWt); }
        }

        [Description("Total dry matter weight of plant's roots")]
        [Units("kgDM/ha")]
        public double RootWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.RootWt); }
        }

        #endregion

        #region - C and DM flows  ------------------------------------------------------------------------------------------

        [Description("Gross potential plant growth (potential C assimilation)")]
        [Units("kgDM/ha")]
        public double PlantGrossPotentialGrowthWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.GrossPotentialGrowthWt); }
        }

        [Description("Respiration rate (DM lost via respiration)")]
        [Units("kgDM/ha")]
        public double PlantRespirationWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.RespirationWt); }
        }

        [Description("C remobilisation (DM remobilised from old tissue to new growth)")]
        [Units("kgDM/ha")]
        public double PlantRemobilisationWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.RemobilisationWt); }
        }

        [Description("Net potential plant growth")]
        [Units("kgDM/ha")]
        public double PlantNetPotentialGrowthWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.NetPotentialGrowthWt); }
        }

        [Description("Potential growth rate after water stress")]
        [Units("kgDM/ha")]
        public double PlantPotGrowthWt_Wstress
        {
            get { return mySward.Sum(mySpecies => mySpecies.PotGrowthWt_Wstress); }
        }

        [Description("Actual plant growth (before littering)")]
        [Units("kgDM/ha")]
        public double PlantActualGrowthWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.ActualGrowthWt); }
        }

        [Description("Effective growth rate, after turnover")]
        [Units("kgDM/ha")]
        public double PlantEffectiveGrowthWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.EffectiveGrowthWt); }
        }

        [Description("Effective herbage (shoot) growth")]
        [Units("kgDM/ha")]
        public double HerbageGrowthWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.HerbageGrowthWt); }
        }

        [Description("Dry matter amount of litter deposited onto soil surface")]
        [Units("kgDM/ha")]
        public double LitterDepositionWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.LitterWt); }
        }

        [Description("Dry matter amount of senescent roots added to soil FOM")]
        [Units("kgDM/ha")]
        public double RootSenescenceWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.RootSenescedWt); }
        }

        [Description("Gross primary productivity")]
        [Units("kgDM/ha")]
        public double GPP
        {
            get { return mySward.Sum(mySpecies => mySpecies.GPP); }
        }

        [Description("Net primary productivity")]
        [Units("kgDM/ha")]
        public double NPP
        {
            get { return mySward.Sum(mySpecies => mySpecies.NPP); }
        }

        [Description("Net above-ground primary productivity")]
        [Units("kgDM/ha")]
        public double NAPP
        {
            get { return mySward.Sum(mySpecies => mySpecies.NAPP); }
        }

        #endregion

        #region - N amounts  -----------------------------------------------------------------------------------------------

        [Description("Total amount of N in plants")]
        [Units("kgN/ha")]
        public double TotalPlantN
        {
            get { return mySward.Sum(mySpecies => mySpecies.TotalN); }
        }

        [Description("Total amount of N in plants above ground")]
        [Units("kgN/ha")]
        public double AboveGroundN
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGroundN); }
        }

        [Description("Total amount of N in plants alive above ground")]
        [Units("kgN/ha")]
        public double AboveGroundLiveN
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGroundLiveN); }
        }

        [Description("Total amount of N in dead plants above ground")]
        [Units("kgN/ha")]
        public double AboveGroundDeadN
        {
            get { return mySward.Sum(mySpecies => mySpecies.AboveGroundDeadN); }
        }

        [Description("Total amount of N in standing plants")]
        [Units("kgN/ha")]
        public double StandingPlantN
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingN); }
        }

        [Description("Total amount of N in standing alive plants")]
        [Units("kgN/ha")]
        public double StandingLivePlantN
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingLiveN); }
        }

        [Description("Total amount of N in dead standing plants")]
        [Units("kgN/ha")]
        public double StandingDeadPlantN
        {
            get { return mySward.Sum(mySpecies => mySpecies.StandingDeadN); }
        }

        [Description("Total amount of N in plants below ground")]
        [Units("kgN/ha")]
        public double BelowGroundN
        {
            get { return mySward.Sum(mySpecies => mySpecies.BelowGroundN); }
        }

        [Description("Total amount of N in the plant's leaves")]
        [Units("kgN/ha")]
        public double LeafN
        {
            get { return mySward.Sum(mySpecies => mySpecies.LeafN); }
        }

        [Description("Total amount of N in the plant's stems")]
        [Units("kgN/ha")]
        public double StemN
        {
            get { return mySward.Sum(mySpecies => mySpecies.StemN); }
        }

        [Description("Total amount of N in the plant's stolons")]
        [Units("kgN/ha")]
        public double StolonN
        {
            get { return mySward.Sum(mySpecies => mySpecies.StolonN); }
        }

        [Description("Total amount of N in the plant's roots")]
        [Units("kgN/ha")]
        public double RootN
        {
            get { return mySward.Sum(mySpecies => mySpecies.RootN); }
        }

        #endregion

        #region - N concentrations  ----------------------------------------------------------------------------------------

        [Description("Proportion of N above ground in relation to below ground")]
        [Units("%")]
        public double ShootRootNPct
        {
            get { return 100 * (AboveGroundN / BelowGroundN); }
        }

        [Description("Average N concentration of standing plants")]
        [Units("kgN/kgDM")]
        public double StandingPlantNConc
        {
            get { return StandingPlantN / StandingPlantWt; }
        }

        [Description("Average N concentration of leaves")]
        [Units("kgN/kgDM")]
        public double LeafNConc
        {
            get { return LeafN / LeafWt; }
        }

        [Description("Average N concentration in stems")]
        [Units("kgN/kgDM")]
        public double StemNConc
        {
            get { return StemN / StemWt; }
        }

        [Description("Average N concentration in stolons")]
        [Units("kgN/kgDM")]
        public double StolonNConc
        {
            get { return StolonN / StolonWt; }
        }

        [Description("Average N concentration in roots")]
        [Units("kgN/kgDM")]
        public double RootNConc
        {
            get { return RootN / RootWt; }
        }

        [Description("Nitrogen concentration in new growth")]
        [Units("kgN/kgDM")]
        public double PlantGrowthNconc
        {
            get { return PlantActualGrowthN / PlantActualGrowthWt; }
        }
        # endregion

        #region - N flows  -------------------------------------------------------------------------------------------------

        [Description("Amount of N remobilised from senescing tissue")]
        [Units("kgN/ha")]
        public double PlantRemobilisedN
        {
            get { return mySward.Sum(mySpecies => mySpecies.RemobilisedN); }
        }

        [Description("Amount of luxury N remobilised")]
        [Units("kgN/ha")]
        public double PlantLuxuryNRemobilised
        {
            get { return mySward.Sum(mySpecies => mySpecies.RemobilisedLuxuryN); }
        }

        [Description("Amount of luxury N potentially remobilisable")]
        [Units("kgN/ha")]
        public double PlantRemobilisableLuxuryN
        {
            get { return mySward.Sum(mySpecies => mySpecies.RemobilisableLuxuryN); }
        }

        [Description("Amount of atmospheric N fixed")]
        [Units("kgN/ha")]
        public double PlantFixedN
        {
            get { return mySward.Sum(mySpecies => mySpecies.FixedN); }
        }

        [Description("Plant nitrogen requirement with luxury uptake")]
        [Units("kgN/ha")]
        public double NitrogenRequiredLuxury
        {
            get { return mySward.Sum(mySpecies => mySpecies.RequiredNLuxury); }
        }

        [Description("Plant nitrogen requirement for optimum growth")]
        [Units("kgN/ha")]
        public double NitrogenRequiredOptimum
        {
            get { return mySward.Sum(mySpecies => mySpecies.RequiredNOptimum); }
        }

        [Description("Plant nitrogen demand from soil")]
        [Units("kgN/ha")]
        public double NitrogenDemand
        {
            get { return mySward.Sum(mySpecies => mySpecies.DemandSoilN); }
        }

        [Description("Plant available nitrogen in each soil layer")]
        [Units("kgN/ha")]
        public double[] NitrogenAvailable
        {
            get
            {
                if (isSwardControlled)
                {
                    return soilAvailableN;
                }
                else
                {
                    double[] result = new double[nLayers];
                    for (int layer = 0; layer < nLayers; layer++)
                        result[layer] = mySward.Sum(mySpecies => mySpecies.SoilAvailableN[layer]);
                    return result;
                }
            }
        }

        [Description("Plant nitrogen uptake from each soil layer")]
        [Units("kgN/ha")]
        public double[] NitrogenUptake
        {
            get
            {
                double[] result = new double[nLayers];
                for (int layer = 0; layer < nLayers; layer++)
                    result[layer] = mySward.Sum(mySpecies => mySpecies.UptakeN[layer]);
                return result;
            }
        }

        [Description("Amount of N deposited as litter onto soil surface")]
        [Units("kgN/ha")]
        public double LitterDepositionN
        {
            get { return mySward.Sum(mySpecies => mySpecies.LitterN); }
        }

        [Description("Amount of N added to soil FOM by senescent roots")]
        [Units("kgN/ha")]
        public double RootSenescenceN
        {
            get { return mySward.Sum(mySpecies => mySpecies.SenescedRootN); }
        }

        [Description("Nitrogen amount in new growth")]
        [Units("kgN/ha")]
        public double PlantActualGrowthN
        {
            get { return mySward.Sum(mySpecies => mySpecies.ActualGrowthN); }
        }

        #endregion

        #region - Turnover and DM allocation  ------------------------------------------------------------------------------

        [Description("Dry matter allocated to shoot")]
        [Units("kgDM/ha")]
        public double DMToShoot
        {
            get { return mySward.Sum(mySpecies => mySpecies.ActualGrowthWt * mySpecies.ShootDMAllocation); }
        }

        [Description("Dry matter allocated to roots")]
        [Units("kgDM/ha")]
        public double DMToRoots
        {
            get { return mySward.Sum(mySpecies => mySpecies.ActualGrowthWt * mySpecies.RootDMAllocation); }
        }

        [Description("Fraction of growth allocated to roots")]
        [Units("0-1")]
        public double FractionGrowthToRoot
        {
            get { return DMToRoots / PlantActualGrowthWt; }
        }

        [Description("Fraction of growth allocated to shoot")]
        [Units("0-1")]
        public double FractionGrowthToShoot
        {
            get { return DMToShoot / PlantActualGrowthWt; }
        }

        #endregion

        #region - LAI and cover  -------------------------------------------------------------------------------------------

        [Description("Total leaf area index")]
        [Units("m^2/m^2")]
        public double TotalLAI
        {
            get { return GreenLAI + DeadLAI; }
        }

        [Description("Leaf area index of green leaves")]
        [Units("m^2/m^2")]
        public double GreenLAI
        {
            get { return mySward.Sum(mySpecies => mySpecies.GreenLAI); }
        }

        [Description("Leaf area index of dead leaves")]
        [Units("m^2/m^2")]
        public double DeadLAI
        {
            get { return mySward.Sum(mySpecies => mySpecies.DeadLAI); }
        }

        [Description("Average light extintion coefficient")]
        [Units("0-1")]
        public double LightExtCoeff
        {
            get
            {
                double result = mySward.Sum(mySpecies => mySpecies.TotalLAI * mySpecies.LightExtentionCoeff)
                              / mySward.Sum(mySpecies => mySpecies.TotalLAI);

                return result;
            }
        }

        [Description("Fraction of soil covered by green leaves")]
        [Units("%")]
        public double Cover_green
        {
            get
            {
                if (GreenLAI == 0)
                    return 0.0;
                else
                    return (1.0 - Math.Exp(-LightExtCoeff * GreenLAI));
            }
        }

        [Description("Fraction of soil covered by dead leaves")]
        [Units("%")]
        public double Cover_dead
        {
            get
            {
                if (DeadLAI == 0)
                    return 0.0;
                else
                    return (1.0 - Math.Exp(-LightExtCoeff * DeadLAI));
            }
        }

        [Description("Fraction of soil covered by plants")]
        [Units("%")]
        public double Cover_tot
        {
            get
            {
                if (TotalLAI == 0) return 0;
                return (1.0 - (Math.Exp(-LightExtCoeff * TotalLAI)));
            }
        }

        [Description("Sward average height")]                 //needed by micromet
        [Units("mm")]
        public double Height
        {
            get { return Math.Max(20.0, HeightFromMass.Value(AboveGroundPlantWt)); } //speciesInSward.Max(mySpecies => mySpecies.Height); }
        }

        #endregion

        #region - Root depth and distribution  -----------------------------------------------------------------------------

        [Description("Depth of root zone")]
        [Units("mm")]
        public double RootZoneDepth
        {
            get { return mySward.Max(mySpecies => mySpecies.RootDepth); }
        }

        [Description("Layer at bottom of root zone")]
        [Units("mm")]
        public double RootFrontier
        {
            get { return mySward.Max(mySpecies => mySpecies.RootFrontier); }
        }

        [Description("Fraction of root dry matter for each soil layer")]
        [Units("0-1")]
        public double[] RootWtFraction
        {
            get
            {
                if (isSwardControlled)
                {
                    return swardRootFraction;
                }
                else
                {
                    double[] result = new double[nLayers];
                    for (int layer = 0; layer < nLayers; layer++)
                        result[layer] = mySward.Sum(mySpecies => mySpecies.RootWt * mySpecies.RootWtFraction[layer]) / RootWt;
                    return result;
                }
            }
        }

        [Description("Root length density")]
        [Units("mm/mm^3")]
        public double[] RLV
        {
            get
            {
                double[] result = new double[nLayers];
                if (isSwardControlled)
                {
                    double Total_Rlength = RootWt * avgSRL;   // m root/ha
                    Total_Rlength *= 0.0000001;  // convert into mm root/mm2 soil)
                    for (int layer = 0; layer < result.Length; layer++)
                    {
                        result[layer] = swardRootFraction[layer] * Total_Rlength / Soil.Thickness[layer];    // mm root/mm3 soil
                    }
                }
                else
                {
                    for (int layer = 0; layer < nLayers; layer++)
                        result[layer] = mySward.Sum(mySpecies => mySpecies.RLV[layer]);
                }
                return result;
            }
        }

        #endregion

        #region - Water amounts  -------------------------------------------------------------------------------------------

        [Description("Plant water demand")]
        [Units("mm")]
        public double PlantWaterDemand
        {
            get { return mySward.Sum(mySpecies => mySpecies.WaterDemand); }
        }

        [Description("Plant available water in soil")]
        [Units("mm")]
        public double[] PlantSoilAvailableWater
        {
            get
            {
                if (isSwardControlled)
                {
                    return soilAvailableWater;
                }
                else
                {
                    double[] result = new double[nLayers];
                    for (int layer = 0; layer < nLayers; layer++)
                        result[layer] = mySward.Sum(mySpecies => mySpecies.SoilAvailableWater[layer]);
                    return result;
                }
            }
        }

        [Description("Plant water uptake from soil")]
        [Units("mm")]
        public double[] PlantWaterUptake
        {
            get
            {
                double[] result = new double[nLayers];
                for (int layer = 0; layer < nLayers; layer++)
                    result[layer] = mySward.Sum(mySpecies => mySpecies.WaterUptake[layer]);
                return result;
            }
        }

        #endregion

        #region - Growth limiting factors  ---------------------------------------------------------------------------------

        [Description("Average plant growth limiting factor due to nitrogen availability")]
        [Units("0-1")]
        public double GLFn
        {
            get { return mySward.Sum(mySpecies => mySpecies.GLFN * mySpecies.AboveGrounLivedWt) / AboveGroundLiveWt; }
        }

        [Description("Average plant growth limiting factor due to plant N concentration")]
        [Units("0-1")]
        public double GLFnConcentration
        {
            get { return mySward.Sum(mySpecies => mySpecies.GLFnConcentration * mySpecies.AboveGrounLivedWt) / AboveGroundLiveWt; }
        }

        [Description("Average plant growth limiting factor due to temperature")]
        [Units("0-1")]
        public double GLFtemp
        {
            get { return mySward.Sum(mySpecies => mySpecies.GLFTemp * mySpecies.AboveGrounLivedWt) / AboveGroundLiveWt; }
        }

        [Description("Average plant growth limiting factor due to water deficit")]
        [Units("0-1")]
        public double GLFwater
        {
            get { return Math.Max(0.0, Math.Min(1.0, PlantWaterUptake.Sum() / PlantWaterDemand)); }
        }

        [Description("Average generic plant growth limiting factor, used for other factors")]
        [Units("0-1")]
        public double GLFrgr
        {
            get { return mySward.Sum(mySpecies => mySpecies.GlfGeneric * mySpecies.AboveGrounLivedWt) / AboveGroundLiveWt; }
        }

        [Description("Effect of vapour pressure on growth (used by micromet)")]
        [Units("0-1")]
        public double FVPD
        {
            get { return mySward[0].FVPD; }
        }

        #endregion

        #region - Harvest variables  ---------------------------------------------------------------------------------------

        [Description("Total dry matter amount available for removal (leaf+stem)")]
        [Units("kgDM/ha")]
        public double HarvestableWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.HarvestableWt); }
        }

        [Description("Amount of plant dry matter removed by harvest")]
        [Units("kgDM/ha")]
        public double HarvestedWt
        {
            get { return mySward.Sum(mySpecies => mySpecies.HarvestedWt); }
        }

        [Description("Amount of N removed by harvest")]
        [Units("kgN/ha")]
        public double HarvestedN
        {
            get { return mySward.Sum(mySpecies => mySpecies.HarvestedN); }
        }

        [Description("average N concentration of harvested material")]
        [Units("kgN/kgDM")]
        public double HarvestedNconc
        {
            get { return HarvestedN / HarvestedWt; }
        }

        [Description("Average digestibility of herbage")]
        [Units("0-1")]
        public double HerbageDigestibility
        {
            get { return mySward.Sum(mySpecies => mySpecies.HerbageDigestibility * mySpecies.StandingWt) / StandingPlantWt; }
        }

        [Description("Average digestibility of harvested material")]
        [Units("0-1")]
        public double HarvestedDigestibility
        {
            get { return mySward.Sum(mySpecies => mySpecies.HarvestedDigestibility * mySpecies.HarvestedWt) / HarvestedWt; }
        }

        [Description("Average ME of herbage")]
        [Units("(MJ/ha)")]
        public double HerbageME
        {
            get { return 16 * HerbageDigestibility * StandingPlantWt; }
        }

        [Description("Average ME of harvested material")]
        [Units("(MJ/ha)")]
        public double HarvestedME
        {
            get { return 16 * HarvestedDigestibility * HarvestedWt; }
        }

        #endregion

        #endregion

        #region Private variables  -----------------------------------------------------------------------------------------

        /// <summary>
        /// flag signialling whether the initialisation procedures have been performed
        /// </summary>
        private bool hasInitialised = false;
       
        /// <summary>
        /// flag signialling whether crop is alive (not killed)
        /// </summary>
        private bool isAlive = true;

        /// <summary>
        /// Number of soil layers
        /// </summary>
        private int nLayers = 0;

        // -- Root variables  -----------------------------------------------------------------------------------------
        /// <summary>
        /// sward root depth (maximumn depth)
        /// </summary>
        private double swardRootDepth;
        /// <summary>
        /// average root distribution over the soil profile
        /// </summary>
        private double[] swardRootFraction;
        /// <summary>
        /// average specific root length
        /// </summary>
        private double avgSRL;

        // -- Water variables  ----------------------------------------------------------------------------------------

        /// <summary>
        /// Amount of soil water available to the sward, from each soil layer (mm)
        /// </summary>
        private double[] soilAvailableWater;
        /// <summary>
        /// Daily soil water demand for the whole sward (mm)
        /// </summary>
        private double swardWaterDemand = 0.0;
        /// <summary>
        /// Soil water uptake for the whole sward, from each soil layer (mm)
        /// </summary>
        private double[] swardWaterUptake;

        // -- Nitrogen variables  -------------------------------------------------------------------------------------

        /// <summary>
        /// amount of soil N available for uptake to the whole sward
        /// </summary>
        private double[] soilAvailableN;
        /// <summary>
        /// amount of NH4 available for uptake to the whole sward
        /// </summary>
        private double[] soilNH4Available;
        /// <summary>
        /// amount of NO3 available for uptake to the whole sward
        /// </summary>
        private double[] soilNO3Available;
        private double swardNFixation = 0.0;
        private double swardNdemand = 0.0;
        private double swardSoilNdemand = 0.0;
        private double[] swardNUptake;
        private double swardSoilNUptake = 0.0;
        private double swardRemobilisedN = 0.0;
        private double swardNRemobNewGrowth = 0.0;
        private double swardNewGrowthN = 0.0;
        private double swardNFastRemob2 = 0.0;
        private double swardNFastRemob3 = 0.0;

        #endregion

        #region Constants  -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Average carbon content in plant dry matter
        /// </summary>
        const double CinDM = 0.4;
        /// <summary>
        /// Nitrogen to protein convertion factor
        /// </summary>
        const double N2Protein = 6.25;

        #endregion

        #region Initialisation methods  ------------------------------------------------------------------------------------

        [EventSubscribe("Loaded")]
        private void OnLoaded()
        {
            // get the number and reference to the mySpecies in the sward
            numSpecies = Apsim.Children(this, typeof(PastureSpecies)).Count;
            mySward = new PastureSpecies[numSpecies];
            int s = 0;
            foreach (PastureSpecies mySpecies in Apsim.Children(this, typeof(PastureSpecies)))
            {
                mySward[s] = mySpecies;
                s += 1;
            }
        }

        [EventSubscribe("Commencing")]
        private void OnSimulationCommencing(object sender, EventArgs e)
        {
            hasInitialised = false;
            // This is needed to perform some initialisation set up at the first day of simulation,
            //  it cannot be done here because it needs the mySpecies to be initialised, which is not 
            //  only happen after this...

            foreach (PastureSpecies mySpecies in mySward)
            {
                mySpecies.isSwardControlled = isSwardControlled;
                mySpecies.myWaterUptakeSource = waterUptakeSource;
                mySpecies.myNitrogenUptakeSource = nUptakeSource;

                if (isSwardControlled)
                {
                    mySpecies.ExpoLinearDepthParam = expoLinearDepthParam;
                    mySpecies.ExpoLinearCurveParam = expoLinearCurveParam;
                }
            }

            // get the number of layers in the soil profile
            nLayers = Soil.Thickness.Length;

            // initialise available N
            swardWaterUptake = new double[nLayers];
            soilAvailableN = new double[nLayers];
        }

        #endregion

        #region Daily processes  -------------------------------------------------------------------------------------------

        /// <summary>
        /// EventHandeler - preparation befor the main process
        /// </summary>
        [EventSubscribe("DoDailyInitialisation")]
        private void OnDoDailyInitialisation(object sender, EventArgs e)
        {
            if (!hasInitialised)
            {
                // perform the initialisation of some variables (done here because it needs data from mySpecies)
                if (isSwardControlled)
                {
                    // root depth and distribution
                    swardRootDepth = mySward.Max(mySpecies => mySpecies.RootDepth);
                    swardRootFraction = RootProfileDistribution();

                    // tell other modules about the existence of this plant
                    DoNewCropEvent();
                }
                avgSRL = mySward.Average(mySpecies => mySpecies.SpecificRootLength);

                hasInitialised = true;
            }

            // Send information about this mySpecies canopy, MicroClimate will compute intercepted radiation and water demand
            if (isSwardControlled)
            {
                foreach (PastureSpecies mySpecies in mySward)
                {
                    // light extintion coefficient - set average for all species
                    mySpecies.plantLightExtCoeff = LightExtCoeff;

                    // plant green cover
                    mySpecies.plantGreenCover = Cover_green;

                    // fraction of light intercepted by each species
                    mySpecies.intRadnFrac = mySpecies.GreenCover / mySward.Sum(aSpecies => aSpecies.GreenCover);
                }

                DoNewCanopyEvent();
            }
        }

        /// <summary>
        /// Performs the plant growth calculations
        /// </summary>
        [EventSubscribe("DoPlantGrowth")]
        private void OnDoPlantGrowth(object sender, EventArgs e)
        {
            if (isAlive)
            {
                foreach (PastureSpecies mySpecies in mySward)
                {
                    // stores the current state for this mySpecies
                    mySpecies.SaveState();

                    // update fraction of intercepted radiation  -  needed for at least untill the radiation budget can be done foreach species
                    if (isSwardControlled)
                        mySpecies.intRadnFrac = mySpecies.GreenCover / mySward.Sum(aSpecies => aSpecies.GreenCover);

                    // step 01 - preparation and potential growth
                    mySpecies.CalcPotentialGrowth();
                }

                // Water demand, supply, and uptake
                DoWaterCalculations();

                // step 02 - Potential growth after water limitations
                foreach (PastureSpecies mySpecies in mySward)
                    mySpecies.CalcGrowthWithWaterLimitations();

                // Nitrogen demand, supply, and uptake
                DoNitrogenCalculations();

                foreach (PastureSpecies mySpecies in mySward)
                {
                    // step 03 - Actual growth after nutrient limitations, but before senescence
                    mySpecies.CalcActualGrowthAndPartition();

                    // step 04 - Effective growth after all limitations and senescence
                    mySpecies.CalcTurnoverAndEffectiveGrowth();
                    swardRemobilisedN += mySpecies.RemobilisableN;
                }

                // step 05 - Send amounts of litter and senesced roots to other modules
                DoSurfaceOMReturn(LitterDepositionWt, LitterDepositionN);
                DoIncorpFomEvent(RootSenescenceWt, RootSenescenceN);


                // update/aggregate some variables
                //swardNRemobNewGrowth = mySward.Sum(mySpecies => mySpecies.RemobilisableN);
            }
        }

        #region - Water uptake process  ------------------------------------------------------------------------------------

        /// <summary>
        /// Provides canopy data for MicroClimate, who will do the energy balance and calc water demand
        /// </summary>
        private void DoNewCanopyEvent()
        {
            if (NewCanopy != null)
            {
                myCanopyData.sender = Name;
                myCanopyData.lai = GreenLAI;
                myCanopyData.lai_tot = TotalLAI;
                myCanopyData.height = Height;
                myCanopyData.depth = Height;			  // canopy depth = canopy height
                myCanopyData.cover = Cover_green;
                myCanopyData.cover_tot = Cover_tot;
                NewCanopy.Invoke(myCanopyData);
            }
        }

        /// <summary>
        /// Gets the water uptake for each layer as calculated by an external module (SWIM)
        /// </summary>
        /// <param name="SoilWater"></param>
        [EventSubscribe("WaterUptakesCalculated")]
        private void OnWaterUptakesCalculated(PMF.WaterUptakesCalculatedType SoilWater)
        {
            for (int iCrop = 0; iCrop < SoilWater.Uptakes.Length; iCrop++)
            {
                if (SoilWater.Uptakes[iCrop].Name == Name)
                {
                    for (int layer = 0; layer < SoilWater.Uptakes[iCrop].Amount.Length; layer++)
                        swardWaterUptake[layer] = SoilWater.Uptakes[iCrop].Amount[layer];
                }
            }
        }

        /// <summary>
        /// Water uptake processes
        /// </summary>
        private void DoWaterCalculations()
        {
            // Find out soil available water
            soilAvailableWater = GetSoilAvailableWater();

            // Get the water demand for all mySpecies
            if (!isSwardControlled)
                swardWaterDemand = mySward.Sum(mySpecies => mySpecies.WaterDemand);

            // Do the water uptake (and partition between mySpecies)
            if (waterUptakeSource.ToLower() == "sward")
            {
                DoSoilWaterUptake();
                // calc and set the glf water for each species (in reality only one of the factors is different from 1.0)
                foreach (PastureSpecies mySpecies in mySward)
                    mySpecies.glfWater = mySpecies.WaterLimitingFactor() * mySpecies.WaterLoggingFactor();
            }
            //else
            //    Water uptake is done by each species or by another apsim module
        }

        /// <summary>
        /// Finds out the amount soil water available (consider all mySpecies)
        /// </summary>
        /// <returns>The amount of water available to plants in each layer</returns>
        private double[] GetSoilAvailableWater()
        {
            double[] result = new double[nLayers];
            SoilCrop soilInfo = (SoilCrop)Soil.Crop(Name);
            double layerFraction = 0.0;   //fraction of soil layer explored by plants
            double layerLL = 0.0;         //LL value for a given layer (minimum of all plants)
            if (useAltWUptake == "no")
            {
                for (int layer = 0; layer <= RootFrontier; layer++)
                {
                    layerFraction = mySward.Max(mySpecies => mySpecies.LayerFractionWithRoots(layer));
                    result[layer] = Math.Max(0.0, Soil.SoilWater.sw_dep[layer] - soilInfo.LL[layer] * Soil.Thickness[layer])
                                  * layerFraction;
                    result[layer] *= soilInfo.KL[layer];
                    // Note: assumes KL and LL defined for AgPasture, ignores the values for each mySpecies
                }
            }
            else
            { // Method implemented by RCichota
                // Available Water is function of root density, soil water content, and soil hydraulic conductivity
                // See GetSoilAvailableWater method in the Species code for details on calculation of each mySpecies
                // Here it is assumed that the actual water available for each layer is the smaller value between the
                //  total theoretical available water (corrected for water status and conductivity) and the sum of 
                //  available water for all mySpecies

                double facCond = 0.0;
                double facWcontent = 0.0;

                // get sum water available for all mySpecies
                double[] sumWaterAvailable = new double[nLayers];
                foreach (PastureSpecies plant in mySward)
                    sumWaterAvailable.Zip(plant.GetSoilAvailableWater(), (x, y) => x + y);

                for (int layer = 0; layer <= RootFrontier; layer++)
                {
                    facCond = 1 - Math.Pow(10, -Soil.Water.KS[layer] / referenceKSuptake);
                    facWcontent = 1 - Math.Pow(10,
                                -(Math.Max(0.0, Soil.SoilWater.sw_dep[layer] - Soil.SoilWater.ll15_dep[layer]))
                                / (Soil.SoilWater.dul_dep[layer] - Soil.SoilWater.ll15_dep[layer]));

                    // theoretical total available water
                    layerFraction = mySward.Max(mySpecies => mySpecies.LayerFractionWithRoots(layer));
                    layerLL = mySward.Min(mySpecies => mySpecies.SpeciesLL[layer]) * Soil.Thickness[layer];
                    result[layer] = Math.Max(0.0, Soil.SoilWater.sw_dep[layer] - layerLL) * layerFraction;

                    // actual available water
                    result[layer] = Math.Min(result[layer] * facCond * facWcontent, sumWaterAvailable[layer]);
                }
            }

            return result;
        }

        /// <summary>
        /// Does the actual water uptake and send the deltas to soil module
        /// </summary>
        /// <remarks>
        /// The amount of water taken up from each soil layer is set per mySpecies
        /// </remarks>
        private void DoSoilWaterUptake()
        {
            PMF.WaterChangedType WaterTakenUp = new PMF.WaterChangedType();
            WaterTakenUp.DeltaWater = new double[nLayers];

            double uptakeFraction = Math.Min(1.0, swardWaterDemand / soilAvailableWater.Sum());
            double speciesFraction = 0.0;

            if (useAltWUptake == "no")
            {
                foreach (PastureSpecies mySpecies in mySward)
                {
                    mySpecies.mySoilWaterTakenUp = new double[nLayers];

                    // partition between mySpecies as function of their demand only
                    speciesFraction = mySpecies.WaterDemand / swardWaterDemand;
                    for (int layer = 0; layer <= RootFrontier; layer++)
                    {
                        mySpecies.mySoilWaterTakenUp[layer] = soilAvailableWater[layer] * uptakeFraction * speciesFraction;
                        WaterTakenUp.DeltaWater[layer] -= mySpecies.mySoilWaterTakenUp[layer];
                    }
                }
            }
            else
            { // Method implemented by RCichota
                // Uptake is distributed over the profile according to water availability,
                //  this means that water status and root distribution have been taken into account

                double[] adjustedWAvailable;

                double[] sumWaterAvailable = new double[nLayers];
                for (int layer = 0; layer < RootFrontier; layer++)
                    sumWaterAvailable[layer] = mySward.Sum(mySpecies => mySpecies.SoilAvailableWater[layer]);

                foreach (PastureSpecies mySpecies in mySward)
                {
                    // get adjusted water available
                    adjustedWAvailable = new double[nLayers];
                    for (int layer = 0; layer < mySpecies.RootFrontier; layer++)
                        adjustedWAvailable[layer] = soilAvailableWater[layer] * mySpecies.SoilAvailableWater[layer] / sumWaterAvailable[layer];

                    // get fraction of demand supplied by the soil
                    uptakeFraction = Math.Min(1.0, mySpecies.WaterDemand / adjustedWAvailable.Sum());

                    // get the actual amounts taken up from each layer
                    mySpecies.mySoilWaterTakenUp = new double[nLayers];
                    for (int layer = 0; layer <= mySpecies.RootFrontier; layer++)
                    {
                        mySpecies.mySoilWaterTakenUp[layer] = adjustedWAvailable[layer] * uptakeFraction;
                        WaterTakenUp.DeltaWater[layer] -= mySpecies.mySoilWaterTakenUp[layer];
                    }
                }
                if (Math.Abs(WaterTakenUp.DeltaWater.Sum() + swardWaterDemand) > 0.0001)
                    throw new Exception("Error on computing water uptake");
            }

            // aggregate all water taken up
            foreach (PastureSpecies mySpecies in mySward)
                swardWaterUptake.Zip(mySpecies.WaterUptake, (x, y) => x + y);

            // send the delta water taken up
            WaterChanged.Invoke(WaterTakenUp);
        }

        #endregion

        #region - Nitrogen uptake process  ---------------------------------------------------------------------------------

        /// <summary>
        /// Performs the computations for N uptake
        /// </summary>
        private void DoNitrogenCalculations()
        {
            // get soil available N
            if (nUptakeSource.ToLower() == "sward")
            {
                GetSoilAvailableN();
                foreach (PastureSpecies mySpecies in mySward)
                {
                    for (int layer = 0; layer < nLayers; layer++)
                    {
                        mySpecies.mySoilAvailableN[layer] = soilAvailableN[layer];
                        mySpecies.mySoilNH4available[layer] = soilNH4Available[layer];
                        mySpecies.mySoilNO3available[layer] = soilNO3Available[layer];
                    }
                }

                // get N demand (optimum and luxury)
                GetNDemand();

                // get N fixation
                swardNFixation = CalcNFixation();

                // evaluate the use of N remobilised and any soil demand
                foreach (PastureSpecies mySpecies in mySward)
                    mySpecies.CalcSoilNDemand();
                swardRemobilisedN =mySward.Sum(mySpecies => mySpecies.RemobilisableN);
                swardNRemobNewGrowth = mySward.Sum(mySpecies => mySpecies.RemobilisedN);
                swardSoilNdemand = mySward.Sum(mySpecies => mySpecies.DemandSoilN);

                // get the amount of N taken up from soil
                swardSoilNUptake = CalcSoilNUptake();

                // send delta N to the soil model
                DoSoilNitrogenUptake();

                // preliminary N budget
                foreach (PastureSpecies mySpecies in mySward)
                    mySpecies.newGrowthN = mySpecies.FixedN + mySpecies.RemobilisedN + mySpecies.UptakeN.Sum();                
                swardNewGrowthN = swardNFixation + swardNRemobNewGrowth + swardSoilNUptake;

                // evaluate whether further remobilisation (from luxury N) is needed
                if (NitrogenRequiredOptimum - swardNewGrowthN > -0.0001)
                { // total N available is not enough for optimum growth, check remobilisation of luxury N
                    foreach (PastureSpecies mySpecies in mySward)
                    {
                        mySpecies.CalcNLuxuryRemob();
                        mySpecies.newGrowthN += mySpecies.RemobLuxuryN2 + mySpecies.RemobLuxuryN3;
                    }
                    swardNFastRemob2 = mySward.Sum(mySpecies => mySpecies.RemobLuxuryN2);
                    swardNFastRemob3 = mySward.Sum(mySpecies => mySpecies.RemobLuxuryN3);
                    swardNewGrowthN += swardNFastRemob2 + swardNFastRemob3;
                }
                //else
                //    there is enough N for at least optimum N content, there is no need for further considerations

                // get the glfN for each species
                foreach (PastureSpecies mySpecies in mySward)
                    mySpecies.glfn = Math.Min(1.0, Math.Max(0.0, mySpecies.ActualGrowthN / mySpecies.RequiredNOptimum));
            }
            //else
            //    N uptake is evaluated by the plant mySpecies or some other module
        }

        /// <summary>
        /// Find out the amount of Nitrogen in the soil available to plants for each soil layer
        /// </summary>
        /// <returns>The amount of N in the soil available to plants (kgN/ha)</returns>
        private void GetSoilAvailableN()
        {
            soilNH4Available = new double[nLayers];
            soilNO3Available = new double[nLayers];
            soilAvailableN = new double[nLayers];
            double layerFraction = 0.0;   //fraction of soil layer explored by plants
            double nK = 0.0;              //N availability factor
            double totWaterUptake = swardWaterUptake.Sum();
            double facWtaken = 0.0;

            for (int layer = 0; layer <= RootFrontier; layer++)
            {
                if (useAltNUptake == "no")
                {
                    // simple way, all N in the root zone is available
                    layerFraction = mySward.Max(mySpecies => mySpecies.LayerFractionWithRoots(layer));
                    soilNH4Available[layer] = Soil.SoilNitrogen.nh4[layer] * layerFraction;
                    soilNO3Available[layer] = Soil.SoilNitrogen.no3[layer] * layerFraction;
                }
                else
                {
                    // Method implemented by RCichota,
                    // N is available following water uptake and a given 'availability' factor (for each N form)

                    facWtaken = swardWaterUptake[layer] / Math.Max(0.0, Soil.SoilWater.sw_dep[layer] - Soil.SoilWater.ll15_dep[layer]);

                    layerFraction = mySward.Max(mySpecies => mySpecies.LayerFractionWithRoots(layer));
                    nK = mySward.Max(mySpecies => mySpecies.kuNH4);
                    soilNH4Available[layer] = Soil.SoilNitrogen.nh4[layer] * nK * layerFraction;
                    soilNH4Available[layer] *= facWtaken;

                    nK = mySward.Max(mySpecies => mySpecies.kuNO3);
                    soilNO3Available[layer] = Soil.SoilNitrogen.no3[layer] * nK * layerFraction;
                    soilNO3Available[layer] *= facWtaken;
                }
                soilAvailableN[layer] = soilNH4Available[layer] + soilNO3Available[layer];
            }
        }

        /// <summary>
        /// Get the N demanded for plant growth (with optimum and luxury uptake) for each mySpecies
        /// </summary>
        private void GetNDemand()
        {
            foreach (PastureSpecies mySpecies in mySward)
                mySpecies.CalcNDemand();
            // get N demand for optimum growth (discount minimum N fixation in legumes)
            swardNdemand = 0.0;
            foreach (PastureSpecies mySpecies in mySward)
                swardNdemand += mySpecies.RequiredNOptimum * (1 - mySpecies.MinimumNFixation);
        }

        /// <summary>
        /// Computes the amout of N fixed for each mySpecies
        /// </summary>
        /// <returns>The total amount of N fixed in the sward</returns>
        private double CalcNFixation()
        {
            foreach (PastureSpecies mySpecies in mySward)
                mySpecies.Nfixation = mySpecies.CalcNFixation();
            return mySward.Sum(mySpecies => mySpecies.FixedN);
        }

        /// <summary>
        /// Computes the amount of N to be taken up from the soil
        /// </summary>
        /// <returns>The amount of N to be taken up from each soil layer</returns>
        private double CalcSoilNUptake()
        {
            double result;
            if (swardSoilNdemand == 0.0)
            { // No demand, no uptake
                result = 0.0;
            }
            else
            {
                if (soilAvailableN.Sum() >= swardSoilNdemand)
                { // soil can supply all remaining N needed
                    result = swardSoilNdemand;
                }
                else
                { // soil cannot supply all N needed. Get the available N and partition between mySpecies
                    result = soilAvailableN.Sum();
                }
            }

            return result;
        }

        /// <summary>
        /// Computes the distribution of N uptake over the soil profile and send the delta to soil module
        /// </summary>
        private void DoSoilNitrogenUptake()
        {
            Soils.NitrogenChangedType NTakenUp = new Soils.NitrogenChangedType();
            NTakenUp.Sender = Name;
            NTakenUp.SenderType = "Plant";
            NTakenUp.DeltaNO3 = new double[nLayers];
            NTakenUp.DeltaNH4 = new double[nLayers];

            double uptakeFraction = 0.0;
            if (soilAvailableN.Sum() > 0.0)
                uptakeFraction = Math.Min(1.0, swardSoilNUptake / soilAvailableN.Sum());
            double speciesFraction = 0.0;
            double layerFraction = 0.0;

            if (useAltNUptake == "no")
            {
                foreach (PastureSpecies mySpecies in mySward)
                {
                    mySpecies.mySoilNUptake = new double[nLayers];

                    // partition between mySpecies as function of their demand only
                    speciesFraction = mySpecies.DemandSoilN / swardSoilNdemand;
                    //speciesFraction = mySpecies.RequiredNOptimum * (1 - mySpecies.MinimumNFixation) / swardNdemand;

                    for (int layer = 0; layer <= RootFrontier; layer++)
                    {
                        layerFraction = mySward.Max(aSpecies => aSpecies.LayerFractionWithRoots(layer));
                        mySpecies.mySoilNUptake[layer] = (Soil.SoilNitrogen.nh4[layer] + Soil.SoilNitrogen.no3[layer]) * layerFraction * uptakeFraction * speciesFraction;
                        NTakenUp.DeltaNH4[layer] -= Soil.SoilNitrogen.nh4[layer] * layerFraction * uptakeFraction * speciesFraction;
                        NTakenUp.DeltaNO3[layer] -= Soil.SoilNitrogen.no3[layer] * layerFraction * uptakeFraction * speciesFraction;
                        if((NTakenUp.DeltaNH4[layer] > Soil.SoilNitrogen.nh4[layer]) || (NTakenUp.DeltaNO3[layer]>Soil.SoilNitrogen.no3[layer]))
                            throw new Exception(" Error on N uptake");
                    }
                }
            }
            else
            { // Method implemented by RCichota,
                // Uptake is distributed over the profile according to N availability,
                //  this means that N and water status as well as root distribution have been taken into account

                double[] adjustedNH4Available;
                double[] adjustedNO3Available;

                double[] sumNH4Available = new double[nLayers];
                double[] sumNO3Available = new double[nLayers];
                for (int layer = 0; layer < RootFrontier; layer++)
                {
                    sumNH4Available[layer] = mySward.Sum(mySpecies => mySpecies.SoilAvailableWater[layer]);
                    sumNO3Available[layer] = mySward.Sum(mySpecies => mySpecies.SoilAvailableWater[layer]);
                }
                foreach (PastureSpecies mySpecies in mySward)
                {
                    // get adjusted N available
                    adjustedNH4Available = new double[nLayers];
                    adjustedNO3Available = new double[nLayers];
                    for (int layer = 0; layer <= mySpecies.RootFrontier; layer++)
                    {
                        adjustedNH4Available[layer] = soilNH4Available[layer] * mySpecies.mySoilNH4available[layer] / sumNH4Available[layer];
                        adjustedNO3Available[layer] = soilNO3Available[layer] * mySpecies.mySoilNO3available[layer] / sumNO3Available[layer];
                    }

                    // get fraction of demand supplied by the soil
                    uptakeFraction = Math.Min(1.0, mySpecies.mySoilNTakeUp / (adjustedNH4Available.Sum() + adjustedNO3Available.Sum()));

                    // get the actual amounts taken up from each layer
                    mySpecies.mySoilNUptake = new double[nLayers];
                    for (int layer = 0; layer <= mySpecies.RootFrontier; layer++)
                    {
                        mySpecies.mySoilNUptake[layer] = (adjustedNH4Available[layer] + adjustedNO3Available[layer]) * uptakeFraction;
                        NTakenUp.DeltaNH4[layer] -= Soil.SoilNitrogen.nh4[layer] * uptakeFraction;
                        NTakenUp.DeltaNO3[layer] -= Soil.SoilNitrogen.no3[layer] * uptakeFraction;
                    }
                }
            }
            double totalUptake = mySward.Sum(mySpecies => mySpecies.UptakeN.Sum());
            if ((Math.Abs(swardSoilNUptake - totalUptake) > 0.0001) || (Math.Abs(NTakenUp.DeltaNH4.Sum() + NTakenUp.DeltaNO3.Sum() + totalUptake) > 0.0001))
                throw new Exception("Error on computing N uptake");

            // do the actual N changes
            NitrogenChanged.Invoke(NTakenUp);
        }

        #endregion

        #endregion

        #region Other processes  -------------------------------------------------------------------------------------------

        //--- Not supported yet  -----------------------------------------
        [EventSubscribe("Sow")]
        private void OnSow(SowType PSow)
        {
            //isAlive = true;
            //ResetZero();
            //for (int s = 0; s < numSpecies; s++)
            //    mySpecies[s].SetInGermination();
        }

        /// <summary>
        /// Kills all the plant mySpecies in the sward (zero variables and set to not alive)
        /// </summary>
        /// <param name="KillData">Fraction of crop to kill (here always 100%)</param>
        [EventSubscribe("KillCrop")]
        private void OnKillCrop(KillCropType KillData)
        {
            foreach (PastureSpecies mySpecies in mySward)
                mySpecies.OnKillCrop(KillData);

            isAlive = false;
        }

        /// <summary>
        /// Harvest (remove DM) the sward
        /// </summary>
        /// <param name="amount">DM amount</param>
        /// <param name="type">How the amount is interpreted (remove or residual)</param>
        public void Harvest(double amount, string type)
        {
            GrazeType GrazeData = new GrazeType();
            GrazeData.amount = amount;
            GrazeData.type = type;
            OnGraze(GrazeData);
        }

        /// <summary>
        /// Graze event, remove DM from sward
        /// </summary>
        /// <param name="GrazeData">How amount of DM to remove is defined</param>
        [EventSubscribe("Graze")]
        private void OnGraze(GrazeType GrazeData)
        {
            if ((!isAlive) || StandingPlantWt == 0)
                return;

            // Get the amount that can potentially be removed
            double amountRemovable = mySward.Sum(mySpecies => mySpecies.HarvestableWt);

            // get the amount required to remove
            double amountRequired = 0.0;
            if (GrazeData.type.ToLower() == "SetResidueAmount".ToLower())
            { // Remove all DM above given residual amount
                amountRequired = Math.Max(0.0, StandingPlantWt - GrazeData.amount);
            }
            else if (GrazeData.type.ToLower() == "SetRemoveAmount".ToLower())
            { // Attempt to remove a given amount
                amountRequired = Math.Max(0.0, GrazeData.amount);
            }
            else
            {
                Console.WriteLine("  AgPasture - Method to set amount to remove not recognized, command will be ignored");
            }
            // get the actual amount to remove
            double amountToRemove = Math.Min(amountRequired, amountRemovable);

            // get the amounts to remove by mySpecies:
            if (amountRequired > 0.0)
            {
                // get the weights for each mySpecies, consider preference and available DM
                double[] tempWeights = new double[numSpecies];
                double[] tempAmounts = new double[numSpecies];
                double tempTotal = 0.0;
                double totalPreference = mySward.Sum(mySpecies => mySpecies.PreferenceForGreenDM + mySpecies.PreferenceForDeadDM);
                for (int s = 0; s < numSpecies; s++)
                {
                    tempWeights[s] = mySward[s].PreferenceForGreenDM + mySward[s].PreferenceForDeadDM;
                    tempWeights[s] += (totalPreference - tempWeights[s]) * (amountToRemove / amountRemovable);
                    tempAmounts[s] = Math.Max(0.0, mySward[s].StandingLiveWt - mySward[s].MinimumGreenWt)
                                   + Math.Max(0.0, mySward[s].StandingDeadWt - mySward[s].MinimumDeadWt);
                    tempTotal += tempAmounts[s] * tempWeights[s];
                }

                // do the actual removal for each mySpecies
                for (int s = 0; s < numSpecies; s++)
                {
                    // get the actual fractions to remove for each mySpecies
                    if (tempTotal > 0.0)
                        mySward[s].fractionHarvest = Math.Max(0.0, Math.Min(1.0, tempWeights[s] * tempAmounts[s] / tempTotal));
                    else
                        mySward[s].fractionHarvest = 0.0;

                    // remove DM and N for each mySpecies (digestibility is also evaluated)
                    mySward[s].RemoveDM(amountToRemove * mySward[s].HarvestedFraction);
                }
            }
        }

        /// <summary>
        /// Remove biomass from sward
        /// </summary>
        /// <remarks>
        /// Greater details on how much and which parts are removed is given
        /// </remarks>
        /// <param name="RemovalData">Info about what and how much to remove</param>
        [EventSubscribe("RemoveCropBiomass")]
        private void Onremove_crop_biomass(RemoveCropBiomassType RemovalData)
        {
            // NOTE: It is responsability of the calling module to check that the amount of 
            //  herbage in each plant part is correct
            // No checking if the removing amount passed in are too much here

            // ATTENTION: The amounts passed should be in g/m^2

            double fractionToRemove = 0.0;

            for (int i = 0; i < RemovalData.dm.Length; i++)			  // for each pool (green or dead)
            {
                string plantPool = RemovalData.dm[i].pool;
                for (int j = 0; j < RemovalData.dm[i].dlt.Length; j++)   // for each part (leaf or stem)
                {
                    string plantPart = RemovalData.dm[i].part[j];
                    double amountToRemove = RemovalData.dm[i].dlt[j] * 10.0;    // convert to kgDM/ha
                    if (plantPool.ToLower() == "green" && plantPart.ToLower() == "leaf")
                    {
                        for (int s = 0; s < numSpecies; s++)		   //for each mySpecies
                        {
                            if (LeafLiveWt - amountToRemove > 0.0)
                            {
                                fractionToRemove = amountToRemove / LeafLiveWt;
                                mySward[s].RemoveFractionDM(fractionToRemove, plantPool, plantPart);
                            }
                        }
                    }
                    else if (plantPool.ToLower() == "green" && plantPart.ToLower() == "stem")
                    {
                        for (int s = 0; s < numSpecies; s++)		   //for each mySpecies
                        {
                            if (StemLiveWt - amountToRemove > 0.0)
                            {
                                fractionToRemove = amountToRemove / StemLiveWt;
                                mySward[s].RemoveFractionDM(fractionToRemove, plantPool, plantPart);
                            }
                        }
                    }
                    else if (plantPool.ToLower() == "dead" && plantPart.ToLower() == "leaf")
                    {
                        for (int s = 0; s < numSpecies; s++)		   //for each mySpecies
                        {
                            if (LeafDeadWt - amountToRemove > 0.0)
                            {
                                fractionToRemove = amountToRemove / LeafDeadWt;
                                mySward[s].RemoveFractionDM(fractionToRemove, plantPool, plantPart);
                            }
                        }
                    }
                    else if (plantPool.ToLower() == "dead" && plantPart.ToLower() == "stem")
                    {
                        for (int s = 0; s < numSpecies; s++)		   //for each mySpecies
                        {
                            if (StemDeadWt - amountToRemove > 0.0)
                            {
                                fractionToRemove = amountToRemove / StemDeadWt;
                                mySward[s].RemoveFractionDM(fractionToRemove, plantPool, plantPart);
                            }
                        }
                    }
                }
            }

            // update digestibility and fractionToHarvest
            for (int s = 0; s < numSpecies; s++)
                mySward[s].RefreshAfterRemove();
        }

        /// <summary>
        /// Return a given amount of DM (and N) to surface organic matter
        /// </summary>
        /// <param name="amountDM">DM amount to return</param>
        /// <param name="amountN">N amount to return</param>
        private void DoSurfaceOMReturn(double amountDM, double amountN)
        {
            if (BiomassRemoved != null)
            {
                Single dDM = (Single)amountDM;

                PMF.BiomassRemovedType BR = new PMF.BiomassRemovedType();
                String[] type = new String[] { "Pasture" };
                Single[] dltdm = new Single[] { (Single)amountDM };
                Single[] dltn = new Single[] { (Single)amountN };
                Single[] dltp = new Single[] { 0 };         // P not considered here
                Single[] fraction = new Single[] { 1 };     // fraction is always 1.0 here

                BR.crop_type = Name;
                BR.dm_type = type;
                BR.dlt_crop_dm = dltdm;
                BR.dlt_dm_n = dltn;
                BR.dlt_dm_p = dltp;
                BR.fraction_to_residue = fraction;
                BiomassRemoved.Invoke(BR);
            }
        }

        /// <summary>
        /// Return scenescent roots to fresh organic matter pool in the soil
        /// </summary>
        /// <param name="amountDM">DM amount to return</param>
        /// <param name="amountN">N amount to return</param>
        private void DoIncorpFomEvent(double amountDM, double amountN)
        {
            Soils.FOMLayerLayerType[] FOMdataLayer = new Soils.FOMLayerLayerType[nLayers];

            // ****  RCichota, Jun/2014
            // root senesced are returned to soil (as FOM) considering return is proportional to root mass

            double dAmtLayer = 0.0; //amount of root litter in a layer
            double dNLayer = 0.0;
            for (int layer = 0; layer < nLayers; layer++)
            {
                dAmtLayer = amountDM * swardRootFraction[layer];
                dNLayer = amountN * swardRootFraction[layer];

                float amt = (float)dAmtLayer;

                Soils.FOMType fomData = new Soils.FOMType();
                fomData.amount = amountDM * swardRootFraction[layer];
                fomData.N = amountN * swardRootFraction[layer];
                fomData.C = amountDM * swardRootFraction[layer] * CinDM;
                fomData.P = 0.0;			  // P not considered here
                fomData.AshAlk = 0.0;		  // Ash not considered here

                Soils.FOMLayerLayerType layerData = new Soils.FOMLayerLayerType();
                layerData.FOM = fomData;
                layerData.CNR = 0.0;	    // not used here
                layerData.LabileP = 0;      // not used here

                FOMdataLayer[layer] = layerData;
            }

            if (IncorpFOM != null)
            {
                Soils.FOMLayerType FOMData = new Soils.FOMLayerType();
                FOMData.Type = "Pasture";
                FOMData.Layer = FOMdataLayer;
                IncorpFOM.Invoke(FOMData);
            }
        }

        #endregion

        #region Functions  -------------------------------------------------------------------------------------------------

        /// <summary>
        /// Placeholder for SoilArbitrator
        /// </summary>
        /// <param name="info"></param>
        /// <returns></returns>
        public List<Soils.UptakeInfo> GetSWUptake(List<Soils.UptakeInfo> info)
        {
            return info;
        }
        
        /// <summary>
        /// Compute the distribution of roots in the soil profile (sum is equal to one)
        /// </summary>
        /// <returns>The proportion of root mass in each soil layer</returns>
        private double[] RootProfileDistribution()
        {
            double[] result = new double[nLayers];
            double sumProportion = 0;

            switch (rootDistributionMethod.ToLower())
            {
                case "homogeneous":
                    {
                        // homogenous distribution over soil profile (same root density throughout the profile)
                        double DepthTop = 0;
                        for (int layer = 0; layer < nLayers; layer++)
                        {
                            if (DepthTop >= swardRootDepth)
                                result[layer] = 0.0;
                            else if (DepthTop + Soil.Thickness[layer] <= swardRootDepth)
                                result[layer] = 1.0;
                            else
                                result[layer] = (swardRootDepth - DepthTop) / Soil.Thickness[layer];
                            sumProportion += result[layer] * Soil.Thickness[layer];
                            DepthTop += Soil.Thickness[layer];
                        }
                        break;
                    }
                case "userdefined":
                    {
                        // distribution given by the user
                        // Option no longer available
                        break;
                    }
                case "expolinear":
                    {
                        // distribution calculated using ExpoLinear method
                        //  Considers homogeneous distribution from surface down to a fraction of root depth (p_ExpoLinearDepthParam)
                        //   below this depth, the proportion of root decrease following a power function (exponent = p_ExpoLinearCurveParam)
                        //   if exponent is one than the proportion decreases linearly.
                        double DepthTop = 0;
                        double DepthFirstStage = swardRootDepth * expoLinearDepthParam;
                        double DepthSecondStage = swardRootDepth - DepthFirstStage;
                        for (int layer = 0; layer < nLayers; layer++)
                        {
                            if (DepthTop >= swardRootDepth)
                                result[layer] = 0.0;
                            else if (DepthTop + Soil.Thickness[layer] <= DepthFirstStage)
                                result[layer] = 1.0;
                            else
                            {
                                if (DepthTop < DepthFirstStage)
                                    result[layer] = (DepthFirstStage - DepthTop) / Soil.Thickness[layer];
                                if ((expoLinearDepthParam < 1.0) && (expoLinearCurveParam > 0.0))
                                {
                                    double thisDepth = Math.Max(0.0, DepthTop - DepthFirstStage);
                                    double Ftop = (thisDepth - DepthSecondStage) * Math.Pow(1 - thisDepth / DepthSecondStage, expoLinearCurveParam) / (expoLinearCurveParam + 1);
                                    thisDepth = Math.Min(DepthTop + Soil.Thickness[layer] - DepthFirstStage, DepthSecondStage);
                                    double Fbottom = (thisDepth - DepthSecondStage) * Math.Pow(1 - thisDepth / DepthSecondStage, expoLinearCurveParam) / (expoLinearCurveParam + 1);
                                    result[layer] += Math.Max(0.0, Fbottom - Ftop) / Soil.Thickness[layer];
                                }
                                else if (DepthTop + Soil.Thickness[layer] <= swardRootDepth)
                                    result[layer] += Math.Min(DepthTop + Soil.Thickness[layer], swardRootDepth) - Math.Max(DepthTop, DepthFirstStage) / Soil.Thickness[layer];
                            }
                            sumProportion += result[layer];
                            DepthTop += Soil.Thickness[layer];
                        }
                        break;
                    }
                default:
                    {
                        throw new Exception("No valid method for computing root distribution was selected");
                    }
            }
            if (sumProportion > 0)
                for (int layer = 0; layer < nLayers; layer++)
                    result[layer] = result[layer] / sumProportion;
            else
                throw new Exception("Could not calculate root distribution");
            return result;
        }

        #endregion
    }
}