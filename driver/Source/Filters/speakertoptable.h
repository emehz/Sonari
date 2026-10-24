/*++

Copyright (c) Microsoft Corporation All Rights Reserved

Module Name:

    speakertoptable.h

Abstract:

    Declaration of topology tables.
--*/

#ifndef _SIMPLEAUDIOSAMPLE_SPEAKERTOPTABLE_H_
#define _SIMPLEAUDIOSAMPLE_SPEAKERTOPTABLE_H_

//
// Endpoint name of the render device. The INF stores the text ("Sonari Virtual Output") under
// MediaCategories\{4399BBE7-4BB0-4511-A86B-8C89DB097EBE} in the driver key.
DEFINE_GUID(SPEAKER_CUSTOM_NAME,
    0x4399bbe7, 0x4bb0, 0x4511, 0xa8, 0x6b, 0x8c, 0x89, 0xdb, 0x09, 0x7e, 0xbe);

//=============================================================================
static
KSDATARANGE SpeakerTopoPinDataRangesBridge[] =
{
 {
   sizeof(KSDATARANGE),
   0,
   0,
   0,
   STATICGUIDOF(KSDATAFORMAT_TYPE_AUDIO),
   STATICGUIDOF(KSDATAFORMAT_SUBTYPE_ANALOG),
   STATICGUIDOF(KSDATAFORMAT_SPECIFIER_NONE)
 }
};

//=============================================================================
static
PKSDATARANGE SpeakerTopoPinDataRangePointersBridge[] =
{
  &SpeakerTopoPinDataRangesBridge[0]
};

//=============================================================================
static
PCPIN_DESCRIPTOR SpeakerTopoMiniportPins[] =
{
  // KSPIN_TOPO_WAVEOUT_SOURCE
  {
    0,
    0,
    0,                                                  // InstanceCount
    NULL,                                               // AutomationTable
    {                                                   // KsPinDescriptor
      0,                                                // InterfacesCount
      NULL,                                             // Interfaces
      0,                                                // MediumsCount
      NULL,                                             // Mediums
      SIZEOF_ARRAY(SpeakerTopoPinDataRangePointersBridge),// DataRangesCount
      SpeakerTopoPinDataRangePointersBridge,            // DataRanges
      KSPIN_DATAFLOW_IN,                                // DataFlow
      KSPIN_COMMUNICATION_NONE,                         // Communication
      &KSCATEGORY_AUDIO,                                // Category
      NULL,                                             // Name
      0                                                 // Reserved
    }
  },
  // KSPIN_TOPO_LINEOUT_DEST
  {
    0,
    0,
    0,                                                  // InstanceCount
    NULL,                                               // AutomationTable
    {                                                   // KsPinDescriptor
      0,                                                // InterfacesCount
      NULL,                                             // Interfaces
      0,                                                // MediumsCount
      NULL,                                             // Mediums
      SIZEOF_ARRAY(SpeakerTopoPinDataRangePointersBridge),// DataRangesCount
      SpeakerTopoPinDataRangePointersBridge,            // DataRanges
      KSPIN_DATAFLOW_OUT,                               // DataFlow
      KSPIN_COMMUNICATION_NONE,                         // Communication
      &KSNODETYPE_LINE_CONNECTOR,                       // Category: line out. Windows names speaker-category
                                                        // endpoints "Speakers" and ignores the custom Name below.
      &SPEAKER_CUSTOM_NAME,                             // Name
      0                                                 // Reserved
    }
  }
};

//=============================================================================
static
KSJACK_DESCRIPTION SpeakerJackDescBridge =
{
    KSAUDIO_SPEAKER_STEREO,
    JACKDESC_RGB(0xB3,0xC9,0x8C),              // Color spec for green
    eConnTypeUnknown,
    eGeoLocFront,
    eGenLocPrimaryBox,
    ePortConnIntegratedDevice,
    TRUE
};

// Only return a KSJACK_DESCRIPTION for the physical bridge pin.
static 
PKSJACK_DESCRIPTION SpeakerJackDescriptions[] =
{
    NULL,
    &SpeakerJackDescBridge
};

//=============================================================================
// No volume or mute nodes: without KSNODETYPE_VOLUME/KSNODETYPE_MUTE, Windows
// applies the endpoint volume and mute in software (the audio engine's volume
// APO), so the Windows controls really change what reaches the loopback.
static
PCCONNECTION_DESCRIPTOR SpeakerTopoMiniportConnections[] =
{
    //  FromNode,                 FromPin,                    ToNode,                 ToPin
    {   PCFILTER_NODE,            KSPIN_TOPO_WAVEOUT_SOURCE,   PCFILTER_NODE,          KSPIN_TOPO_LINEOUT_DEST }
};

//=============================================================================
static
PCPROPERTY_ITEM PropertiesSpeakerTopoFilter[] =
{
    {
        &KSPROPSETID_Jack,
        KSPROPERTY_JACK_DESCRIPTION,
        KSPROPERTY_TYPE_GET |
        KSPROPERTY_TYPE_BASICSUPPORT,
        PropertyHandler_SpeakerTopoFilter
    },
    {
        &KSPROPSETID_Jack,
        KSPROPERTY_JACK_DESCRIPTION2,
        KSPROPERTY_TYPE_GET |
        KSPROPERTY_TYPE_BASICSUPPORT,
        PropertyHandler_SpeakerTopoFilter
    }
};

DEFINE_PCAUTOMATION_TABLE_PROP(AutomationSpeakerTopoFilter, PropertiesSpeakerTopoFilter);

//=============================================================================
static
PCFILTER_DESCRIPTOR SpeakerTopoMiniportFilterDescriptor =
{
  0,                                            // Version
  &AutomationSpeakerTopoFilter,                 // AutomationTable
  sizeof(PCPIN_DESCRIPTOR),                     // PinSize
  SIZEOF_ARRAY(SpeakerTopoMiniportPins),        // PinCount
  SpeakerTopoMiniportPins,                      // Pins
  sizeof(PCNODE_DESCRIPTOR),                    // NodeSize
  0,                                            // NodeCount
  NULL,                                         // Nodes
  SIZEOF_ARRAY(SpeakerTopoMiniportConnections), // ConnectionCount
  SpeakerTopoMiniportConnections,               // Connections
  0,                                            // CategoryCount
  NULL                                          // Categories
};

#endif // _SIMPLEAUDIOSAMPLE_SPEAKERTOPTABLE_H_
