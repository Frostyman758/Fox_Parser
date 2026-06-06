// Based on TwpfTool/TwpParamTagGroup.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace MgsvModBldr.Tools.Twpf
{
    public enum ParamType : byte
    {
        Float = 1,
        Vector3 = 2,
        PathId = 3,
        StringId = 4
    }
    public enum ParamName : ushort
    {
        //TppGlobalVolumetricFog
        TppGlobalVolumetricFog_density=257,
        TppGlobalVolumetricFog_color=258,
        TppGlobalVolumetricFog_GZ_albedo=259,//only other color after color
        TppGlobalVolumetricFog_luminance=260,
        TppGlobalVolumetricFog_GZ_height=261,//135 in pouring, like gntn_nightRain_sky
        TppGlobalVolumetricFog_nearDistance=262,
        TppGlobalVolumetricFog_GZ_bottom=263,//0 except ombs which is -10 in e20015_sky
        TppGlobalVolumetricFog_skyAlbedo=264,
        TppGlobalVolumetricFog_rayleighScattering=265,
        TppGlobalVolumetricFog_mieScattering=266,
        TppGlobalVolumetricFog_falloff=267,
        TppGlobalVolumetricFog_mieAnisotropy=268,
        TppGlobalVolumetricFog_exposureOffset0=269,
        TppGlobalVolumetricFog_exposureOffset0_Ev=270,
        TppGlobalVolumetricFog_exposureOffset1=271,
        TppGlobalVolumetricFog_exposureOffset1_Ev=272,
        TppGlobalVolumetricFog_exposureOffset2=273,
        TppGlobalVolumetricFog_exposureOffset2_Ev=274,
        TppGlobalVolumetricFog_power=275,
        TppGlobalVolumetricFog_fogDirLightGain=276,
        TppGlobalVolumetricFog_density_envelope0_add = 277,
        TppGlobalVolumetricFog_density_noise0_ratio = 278,
        // TppAtmosphere
        TppAtmosphere_GZ_mieAnisotropy=513,//twpfs go from 0.2 at night to 1 in day, datasets do 0.8 and ofCloudySky 0.2
        TppAtmosphere_daySkyAmbientScale=514, //0 in avatar_space and low in volginride, 0.5 in nonsunny
        TppAtmosphere_GZ_multiScatteringOrder=515,
        TppAtmosphere_starLight=516,
        TppAtmosphere_cloudiness=517,
        TppAtmosphere_skyColorSunScale=518,
        TppAtmosphere_skyLightSunScale=519,
        //520 0 at most times, 7 at midnight cloudy, 4.5 at midnight rainy, 0 in title
        TppAtmosphere_shadowMaskSpecular=521,
        TppAtmosphere_shadowRange=522,
        TppAtmosphere_shadowRangeExtra=523,
        TppAtmosphere_hiResShadowRange=524,
        TppAtmosphere_shadowProjectionRange=525,
        TppAtmosphere_shadowfadeRange=526,
        TppAtmosphere_selfShadowBias=527,
        TppAtmosphere_shadowRangeLimit=528,
        TppAtmosphere_shadowRangeFoculLengthConnectionScale=529,
        TppAtmosphere_influenceOfFog=530,

        TppAtmosphere_dirLightFadeStart=549,
        TppAtmosphere_dirLightFadeLength=550,
        TppAtmosphere_dirLightFadeEnable=551,
        TppAtmosphere_fogFalloff=552,
        TppAtmosphere_fogFalloffStart=553,

        TppAtmosphere_followCameraEnable=556,
        TppAtmosphere_offsetPosY=557,
        TppAtmosphere_skyColor=558,
        TppAtmosphere_skyEnable=559, //cypr group_photo is 0, but cypr default and other uses is 1
        TppAtmosphere_enviromentSpecular_ShScale0=560,
        TppAtmosphere_enviromentSpecular_ShScale0_Ev=561,
        TppAtmosphere_enviromentSpecular_ShScale1=562,
        TppAtmosphere_enviromentSpecular_ShScale1_Ev=563,
        TppAtmosphere_enviromentSpecular_ShScale2=564,
        TppAtmosphere_enviromentSpecular_ShScale2_Ev=565,
        TppAtmosphere_enviromentSpecular_minFrontReflectionRate=566,
        TppAtmosphere_enviromentSpecular_Color=567,
        TppAtmosphere_enableSteppedMoveOfDirectionalLight=568,
        TppAtmosphere_divisionNumOfSteppedMove=569,
        TppAtmosphere_interpolateTimeInSecond=570,

        TppAtmosphere_starLuminanceScale=576,
        TppAtmosphere_skyLightSunScaleOcean=577,
        TppAtmosphere_influenceOfFogMin=578,

        //TppSky
        TppSky_diffusion=641,
        TppSky_cloudDensity=642,
        TppSky_dirLightGain=643,
        TppSky_inCloudScatterGain=644,
        TppSky_ambLightGain=645,
        TppSky_cylCloudDensity=646,
        TppSky_cylBackScatGain=647,
        TppSky_cylFrontDirGain=648,
        TppSky_cylFrontAmbGain=649,
        TppSky_cloudInfluenceOfFog=650,
        TppSky_cloudFogFalloff=651,
        TppSky_cloudFogFalloffStart=652,
        TppSky_dom2Density=653,
        TppSky_dom3Density=654,
        TppSky_cloudDensityMax=655,
        TppSky_dom2DensityMax=656,
        TppSky_dom3DensityMax=657,
        TppSky_cylCloudDensityMax=658,
        TppSky_cloudInfluenceOfFogMin=659,

        //GrPluginSettings
        GrPluginSettings_minExposure=769,
        GrPluginSettings_maxExposure=770,

        GrPluginSettings_exposureCompensation=772,

        GrPluginSettings_bloomSize=775,
        GrPluginSettings_shutterSpeed=776,
        GrPluginSettings_keyValue=777,

        GrPluginSettings_addExpComp0=782,
        GrPluginSettings_addExpComp0_Ev=783,
        GrPluginSettings_addExpComp1=784,
        GrPluginSettings_addExpComp1_Ev=785,
        GrPluginSettings_addExpComp2=786,
        GrPluginSettings_addExpComp2_Ev=787,
        GrPluginSettings_bloomBrightnessExtraction=788,
        GrPluginSettings_bloomWeight=789,
        GrPluginSettings_ghostStrength=790,
        GrPluginSettings_ghostScreenScale=791,

        //ColorCorrection
        ColorCorrection_colorScale=1025,
        ColorCorrection_startSlope=1027,
        ColorCorrection_endSlope=1028,
        ColorCorrection_lut=1030,
        ColorCorrection_exposureThreshold=1031,
        ColorCorrection_colorScale2=1032,
        ColorCorrection_startSlope2=1033,
        ColorCorrection_endSlope2=1034,
        ColorCorrection_lut2=1035,
        ColorCorrection_exposureThreshold2=1036,

        //LineSSAOParameters
        LineSSAOParameters_innerRadius=1281,
        LineSSAOParameters_outerRadius=1282,
        LineSSAOParameters_maxDistanceInner=1283,
        LineSSAOParameters_maxDistanceOuter=1284,
        LineSSAOParameters_GZ_contrast=1285,
        LineSSAOParameters_blurRadius=1286,
        LineSSAOParameters_falloffStart=1287,
        LineSSAOParameters_falloffRange=1288,
        LineSSAOParameters_gainonStart=1289,
        LineSSAOParameters_gainonRange=1290,
        LineSSAOParameters_contrastLow=1291,
        LineSSAOParameters_contrastHigh=1292,
        LineSSAOParameters_maxDistanceThresholdInner=1293,
        LineSSAOParameters_maxDistanceThresholdOuter=1294,

        //WeatherParameters
        WeatherParameters_raininess=1793,//rain strength (RAINY always 1, in gntn RAINY is 0.5 and POURING is 1, other weathers 0)
        WeatherParameters_windSpeed=1794,
        WeatherParameters_speedTurbulentRate=1795,
        WeatherParameters_speedTurbulentCycle=1796,

        //NoiseEnvelopeGenerator
        NoiseEnvelopeGenerator_envelopeAttackTime=2049,
        NoiseEnvelopeGenerator_envelopeDecayTime=2050,
        NoiseEnvelopeGenerator_noiseRate=2051,

        //LocalReflectionSettings
        LocalReflectionSettings_effectScale=2305,
        LocalReflectionSettings_roughnessBias=2306,
        LocalReflectionSettings_rayStepScale=2307,
        LocalReflectionSettings_rayCount=2308,
        LocalReflectionSettings_shaderType=2309,
        LocalReflectionSettings_fade=2310,
        LocalReflectionSettings_mask=2311,

        //GenerativeClouds
        GenerativeClouds_gcEnable=2561,
        GenerativeClouds_gcDensity=2562,
        GenerativeClouds_gcDensityMax=2563,
        GenerativeClouds_gcScattering=2564,
        GenerativeClouds_gcAbsorption=2565,
        GenerativeClouds_gcMieAnisotropy=2566,
        GenerativeClouds_gcDirLightGain=2567,
        GenerativeClouds_gcSkyLightGain=2568,
        GenerativeClouds_gcRayMarchDepth=2569,
        GenerativeClouds_gcExponent0=2570,
        GenerativeClouds_gcExponent0Max=2571,
        GenerativeClouds_gcExponent1=2572,
        GenerativeClouds_gcExponent1Max=2573,
    };
    [XmlType]
    public class TwpParamTagGroup
    {
        [XmlAttribute]
        public ParamType paramType;
        [XmlAttribute]
        public string paramName;
        [XmlElement]
        public List<TwpParamTagDefs> paramTagDefs;

        public void Read(BinaryReader reader, Dictionary<ulong, string> dict)
        {
            byte paramTagDefCount = reader.ReadByte();
            paramType = (ParamType)reader.ReadByte();
            paramName = ((ParamName)reader.ReadUInt16()).ToString();
            if (TwpfLog.IsVerbose)
                Console.WriteLine($"Param tag def count: {paramTagDefCount}, Param type: {paramType}, Param name: {paramName}");

            int[] paramTagDefOffsets = new int[paramTagDefCount];
            for (int i = 0; i < paramTagDefCount; i++)
            {
                paramTagDefOffsets[i] = reader.ReadInt32();
                if (TwpfLog.IsVerbose)
                    Console.WriteLine($"Offset #{i}: {paramTagDefOffsets[i]}");
            }
            paramTagDefs = new List<TwpParamTagDefs>();
            foreach (int paramTagDefOffset in paramTagDefOffsets)
            {
                reader.BaseStream.Position = paramTagDefOffset;
                TwpParamTagDefs paramTagDef = new TwpParamTagDefs();
                paramTagDef.Read(reader, paramType, dict);
                paramTagDefs.Add(paramTagDef);
            }
        }
        public void Write(BinaryWriter writer)
        {
            writer.Write((byte)paramTagDefs.Count);
            writer.Write((byte)paramType);
            if (Enum.TryParse(paramName,true,out ParamName paramNameEnum))
                writer.Write((ushort)paramNameEnum);
            else if (ushort.TryParse(paramName, out ushort paramNameId))
                writer.Write(paramNameId);
            else
                throw new ArgumentOutOfRangeException();

            long offsetToParamTagDefs = writer.BaseStream.Position;
            for (int i = 0; i < paramTagDefs.Count; i++)
                writer.Write(0);

            foreach (TwpParamTagDefs paramTagDef in paramTagDefs)
            {
                int index = paramTagDefs.IndexOf(paramTagDef);

                long returnPos = writer.BaseStream.Position;
                writer.BaseStream.Position = offsetToParamTagDefs + (index * 4);
                writer.Write((int)returnPos);
                writer.BaseStream.Position = returnPos;

                paramTagDef.Write(writer, paramType);
            }
        }
    }
}
