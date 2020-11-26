﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace PnP.Core.Model.SharePoint
{
    internal static class ListDataAsStreamHandler
    {
        internal static async Task<Dictionary<string, object>> Deserialize(string json, List list)
        {
            if (list == null)
            {
                throw new ArgumentNullException(nameof(list));
            }

            if (string.IsNullOrEmpty(json))
            {
                throw new ArgumentNullException(nameof(json));
            }

            var result = new Dictionary<string, object>();

            var document = JsonSerializer.Deserialize<JsonElement>(json);

            // Process "non-rows" data
            foreach (var property in document.EnumerateObject())
            {
                // The rows are handled seperately
                if (property.Name != "Row")
                {
                    FieldType fieldType = FieldType.Text;
                    if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        fieldType = FieldType.Integer;
                    }
                    else if (property.Value.ValueKind == JsonValueKind.False || property.Value.ValueKind == JsonValueKind.True)
                    {
                        fieldType = FieldType.Boolean;
                    }

                    var fieldValue = GetJsonPropertyValue(property.Value, fieldType);

                    if (!result.ContainsKey(property.Name))
                    {
                        result.Add(property.Name, fieldValue);
                    }
                }
            }

            // Process rows
            if (document.TryGetProperty("Row", out JsonElement dataRows))
            {
                // Mark collection as requested to avoid our linq integration to actually execute this as a query to SharePoint
                list.Items.Requested = true;

                // No data returned, stop processing
                if (dataRows.GetArrayLength() == 0)
                {
                    return result;
                }

                // Load the fields if not yet loaded
                await list.EnsurePropertiesAsync(List.LoadFieldsExpression).ConfigureAwait(false);

                // Grab the list entity information
                var entityInfo = EntityManager.Instance.GetStaticClassInfo(list.GetType());

                // Process the returned data rows
                foreach (var row in dataRows.EnumerateArray())
                {
                    if (int.TryParse(row.GetProperty("ID").GetString(), out int listItemId))
                    {
                        var itemToUpdate = list.Items.FirstOrDefault(p => p.Id == listItemId);
                        if (itemToUpdate == null)
                        {
                            itemToUpdate = (list.Items as ListItemCollection).CreateNewAndAdd();
                        }

                        itemToUpdate = itemToUpdate as ListItem;
                        itemToUpdate.SetSystemProperty(p => p.Id, listItemId);

                        // Ensure metadata handling when list items are read using this method
                        await (itemToUpdate as ListItem).GraphToRestMetadataAsync().ConfigureAwait(false);

                        var overflowDictionary = itemToUpdate.Values;

                        // Translate this row first into an object model for easier consumption
                        var rowToProcess = TransformRowData(row, list.Fields, overflowDictionary);

                        foreach (var property in rowToProcess)
                        {
                            if (property.Name == "ID")
                            {
                                // already handled, so continue
                            }
                            //else if (property.Name == "_CommentFlags")
                            //{
                            //    string commentsFlags = row.GetProperty("_CommentFlags").GetString();
                            //    if (bool.TryParse(commentsFlags, out bool commentsEnabled))
                            //    {
                            //        itemToUpdate.SetSystemProperty(p => p.CommentsDisabled, commentsEnabled);
                            //    }
                            //}
                            // Handle the overflow fields
                            else
                            {
                                object fieldValue = null;
                                if (!property.Values.Any())
                                {
                                    if (property.Value.ValueKind == JsonValueKind.Array)
                                    {
                                        // MultiChoice property
                                        fieldValue = new List<string>();
                                        foreach(var prop in property.Value.EnumerateArray())
                                        {
                                            (fieldValue as List<string>).Add(prop.GetString());
                                        }
                                    }
                                    else
                                    {
                                        // simple property
                                        fieldValue = GetJsonPropertyValue(property.Value, property.Type);
                                    }
                                }
                                else
                                {
                                    // Special field
                                    if (!property.IsArray)
                                    {
                                        var listDataAsStreamPropertyValue = property.Values.First();
                                        fieldValue = listDataAsStreamPropertyValue.FieldValue.FromListDataAsStream(listDataAsStreamPropertyValue.Properties);
                                        (fieldValue as FieldValue).IsArray = false;
                                    }
                                    else
                                    {
                                        fieldValue = new FieldValueCollection(property.Field, property.Name, overflowDictionary);
                                        foreach(var xx in property.Values)
                                        {
                                            var yy = xx.FieldValue.FromListDataAsStream(xx.Properties);
                                            if (yy is FieldLookupValue yyLookup)
                                            {
                                                // Only add to collection when it points to a real value
                                                if (yyLookup.LookupId > -1)
                                                {
                                                    yyLookup.IsArray = true;
                                                    (fieldValue as FieldValueCollection).Values.Add(yyLookup);
                                                }
                                            }
                                        }
                                    }
                                }

                                if (!overflowDictionary.ContainsKey(property.Name))
                                {
                                    overflowDictionary.SystemAdd(property.Name, fieldValue);
                                }
                                else
                                {
                                    overflowDictionary.SystemUpdate(property.Name, fieldValue);
                                }
                            }
                        }
                    }
                }
            }

            return result;
        }

        private static List<ListDataAsStreamProperty> TransformRowData(JsonElement row, IFieldCollection fields, TransientDictionary overflowDictionary)
        {
            List<ListDataAsStreamProperty> properties = new List<ListDataAsStreamProperty>();

            foreach (var property in row.EnumerateObject())
            {                
                var field = fields.FirstOrDefault(p => p.InternalName == property.Name);
                if (field != null)
                {
                    var streamProperty = new ListDataAsStreamProperty()
                    {
                        Field = field,
                        Type = field.FieldTypeKind,
                        Name = property.Name
                    };

                    // Is this a field that needs to be wrapped into a special field type?
                    var specialField = DetectSpecialFieldType(streamProperty.Name, overflowDictionary, field);
                    if (specialField != null)
                    {
                        streamProperty.IsArray = specialField.Item2;

                        if (property.Value.ValueKind == JsonValueKind.Array)
                        {
                            #region Sample json responses
                            /*
                            "PersonSingle": [
                                {
                                    "id": "15",
                                    "title": "Kevin Cook",
                                    "email": "KevinC@bertonline.onmicrosoft.com",
                                    "sip": "KevinC@bertonline.onmicrosoft.com",
                                    "picture": ""
                                }
                            ],

                            "PersonMultiple": [
                                {
                                    "id": "14",
                                    "value": "Anna Lidman",
                                    "title": "Anna Lidman",
                                    "email": "AnnaL@bertonline.onmicrosoft.com",
                                    "sip": "AnnaL@bertonline.onmicrosoft.com",
                                    "picture": ""
                                },
                                {
                                    "id": "6",
                                    "value": "Bert Jansen (Cloud)",
                                    "title": "Bert Jansen (Cloud)",
                                    "email": "bert.jansen@bertonline.onmicrosoft.com",
                                    "sip": "bert.jansen@bertonline.onmicrosoft.com",
                                    "picture": ""
                                }
                            ],

                            "MMSingle": {
                                "__type": "TaxonomyFieldValue:#Microsoft.SharePoint.Taxonomy",
                                "Label": "LBI",
                                "TermID": "ed5449ec-4a4f-4102-8f07-5a207c438571"
                            },

                            "MMMultiple": [
                                {
                                    "Label": "LBI",
                                    "TermID": "ed5449ec-4a4f-4102-8f07-5a207c438571"
                                },
                                {
                                    "Label": "MBI",
                                    "TermID": "1824510b-00e1-40ac-8294-528b1c9421e0"
                                },
                                {
                                    "Label": "HBI",
                                    "TermID": "0b709a34-a74e-4d07-b493-48041424a917"
                                }
                            ],
                             
                            "LookupSingle": [
                                {
                                    "lookupId": 71,
                                    "lookupValue": "Sample Document 01",
                                    "isSecretFieldValue": false
                                }
                            ],

                            "LookupMultiple": [
                                {
                                    "lookupId": 1,
                                    "lookupValue": "General",
                                    "isSecretFieldValue": false
                                },
                                {
                                    "lookupId": 71,
                                    "lookupValue": "Sample Document 01",
                                    "isSecretFieldValue": false
                                }
                            ],
                             
                            "Location": {
                                "DisplayName": null,
                                "LocationUri": "https://www.bingapis.com/api/v6/addresses/QWRkcmVzcy83MDA5ODMwODI3MTUyMzc1ODA5JTdjMT9h%3d%3d?setLang=en",
                                "EntityType": null,
                                "Address": {
                                    "Street": "Somewhere",
                                    "City": "XYZ",
                                    "State": "Vlaanderen",
                                    "CountryOrRegion": "Belgium",
                                    "PostalCode": "9999"
                                },
                                "Coordinates": {
                                    "Latitude": null,
                                    "Longitude": null
                                }
                            },                             
                            */
                            #endregion

                            foreach(var streamPropertyElement in property.Value.EnumerateArray())
                            {
                                (var fieldValue, var isArray) = DetectSpecialFieldType(streamProperty.Name, overflowDictionary, field);
                                var listDataAsStreamPropertyValue = new ListDataAsStreamPropertyValue()
                                {
                                    FieldValue = fieldValue
                                };

                                foreach (var streamPropertyElementValue in streamPropertyElement.EnumerateObject())
                                {
                                    listDataAsStreamPropertyValue.Properties.Add(streamPropertyElementValue.Name, GetJsonPropertyValueAsString(streamPropertyElementValue.Value));
                                }

                                streamProperty.Values.Add(listDataAsStreamPropertyValue);
                            }
                        }
                        else
                        {
                            /*
                             "Url": "https:\u002f\u002fpnp.com\u002f3",
                             "Url.desc": "something3",
                            */

                            var listDataAsStreamPropertyValue = new ListDataAsStreamPropertyValue()
                            {
                                FieldValue = specialField.Item1
                            };

                            if (property.Value.ValueKind == JsonValueKind.Object)
                            {
                                foreach (var streamPropertyElementValue in property.Value.EnumerateObject())
                                {
                                    if (streamPropertyElementValue.Value.ValueKind == JsonValueKind.Object)
                                    {
                                        foreach (var streamPropertyElementValueLevel2 in streamPropertyElementValue.Value.EnumerateObject())
                                        {
                                            listDataAsStreamPropertyValue.Properties.Add(streamPropertyElementValueLevel2.Name, GetJsonPropertyValueAsString(streamPropertyElementValueLevel2.Value));
                                        }
                                    }
                                    else
                                    {
                                        listDataAsStreamPropertyValue.Properties.Add(streamPropertyElementValue.Name, GetJsonPropertyValueAsString(streamPropertyElementValue.Value));
                                    }
                                }
                            }
                            else
                            {
                                listDataAsStreamPropertyValue.Properties.Add(property.Name, GetJsonPropertyValueAsString(property.Value));
                            }

                            streamProperty.Values.Add(listDataAsStreamPropertyValue);
                        }
                    }
                    else
                    {
                        // Add as single property or simple choice collection

                        /*
                        "Title": "Item1",

                        "ChoiceMultiple": [
                            "Choice 1",
                            "Choice 3",
                            "Choice 4"
                        ],
                         */
                        streamProperty.Value = property.Value;
                    }

                    properties.Add(streamProperty);
                }
                else
                {
                    /*
                     "Url.desc": "something3",
                    */

                    if (property.Name.Contains("."))
                    {
                        string[] nameParts = property.Name.Split(new char[] { '.' });

                        var propertyToUpdate = properties.FirstOrDefault(p => p.Name == nameParts[0]);
                        if (propertyToUpdate != null && propertyToUpdate.Values.Count == 1 && !string.IsNullOrEmpty(nameParts[1]))
                        {
                            var valueToUpdate = propertyToUpdate.Values.FirstOrDefault();
                            if (valueToUpdate == null)
                            {
                                valueToUpdate = new ListDataAsStreamPropertyValue();
                                propertyToUpdate.Values.Add(valueToUpdate);
                            }
                            if (!valueToUpdate.Properties.ContainsKey(nameParts[1]))
                            {
                                valueToUpdate.Properties.Add(nameParts[1], GetJsonPropertyValueAsString(property.Value));
                            }
                        }
                    }
                }
            }

            return properties;
        }

        private static Tuple<FieldValue, bool> DetectSpecialFieldType(string name, TransientDictionary dictionary, IField field)
        { 
            switch (field.TypeAsString)
                {
                case "URL": return new Tuple<FieldValue, bool>(new FieldUrlValue(name, dictionary) { Field = field }, false);
                case "UserMulti": return new Tuple<FieldValue, bool>(new FieldUserValue(name, dictionary) { Field = field }, true);
                case "User": return new Tuple<FieldValue, bool>(new FieldUserValue(name, dictionary) { Field = field }, false);
                case "LookupMulti": return new Tuple<FieldValue, bool>(new FieldLookupValue(name, dictionary) { Field = field }, true);
                case "Location": return new Tuple<FieldValue, bool>(new FieldLocationValue(name, dictionary) { Field = field }, false);
                case "TaxonomyFieldTypeMulti": return new Tuple<FieldValue, bool>(new FieldTaxonomyValue(name, dictionary) { Field = field }, true);
                case "TaxonomyFieldType": return new Tuple<FieldValue, bool>(new FieldTaxonomyValue(name, dictionary) { Field = field }, false);

                default:
                    {
                        return null;
                    }
            }
        }

        private static string GetJsonPropertyValueAsString(JsonElement propertyValue)
        {
            if (propertyValue.ValueKind == JsonValueKind.True || propertyValue.ValueKind == JsonValueKind.False)
            {
                return propertyValue.GetBoolean().ToString();
            }
            else if (propertyValue.ValueKind == JsonValueKind.Number)
            {
                return propertyValue.GetInt32().ToString();
            }
            else if (propertyValue.ValueKind == JsonValueKind.Undefined)
            {
                return "Null";
            }
            else
            {
                return propertyValue.GetString();
            }
        }

        private static object GetJsonPropertyValue(JsonElement propertyValue, FieldType fieldType)
        {
            switch (fieldType)
            {
                case FieldType.Boolean:
                    {
                        if (propertyValue.ValueKind == JsonValueKind.True || propertyValue.ValueKind == JsonValueKind.False)
                        {
                            return propertyValue.GetBoolean();
                        }
                        else if (propertyValue.ValueKind == JsonValueKind.String)
                        {
                            if (bool.TryParse(propertyValue.GetString(), out bool parsedBool))
                            {
                                return parsedBool;
                            }
                        }
                        else if (propertyValue.ValueKind == JsonValueKind.Number)
                        {
                            var number = propertyValue.GetInt32();

                            if (number == 1)
                            {
                                return true;
                            }
                            else if (number == 0)
                            {
                                return false;
                            }
                        }

                        // last result, return default bool value
                        return false;
                    }
                case FieldType.Integer:
                    {
                        if (propertyValue.ValueKind != JsonValueKind.Number)
                        {
                            // Override parsing in case it's not a number, assume string
                            if (int.TryParse(propertyValue.GetString(), out int intValue))
                            {
                                return intValue;
                            }
                            else
                            {
                                return 0;
                            }
                        }
                        else
                        {
                            return propertyValue.GetInt32();
                        }
                    }
                case FieldType.Number:
                    {
                        if (propertyValue.ValueKind != JsonValueKind.Number)
                        {
                            if (double.TryParse(propertyValue.GetString(), out double doubleValue))
                            {
                                return doubleValue;
                            }
                            else
                            {
                                return 0.0d;
                            }
                        }
                        else
                        {
                            return propertyValue.GetDouble();
                        }
                    }
                case FieldType.DateTime:
                    {
                        if (propertyValue.ValueKind != JsonValueKind.Null)
                        {
                            if (propertyValue.TryGetDateTime(out DateTime dateTime))
                            {
                                return dateTime;
                            }
                            else
                            {
                                if (DateTime.TryParseExact(propertyValue.GetString(),
                                                           new string[] { "MM/dd/yyyy" },
                                                           System.Globalization.CultureInfo.InvariantCulture,
                                                           System.Globalization.DateTimeStyles.None,
                                                           out DateTime dateTime2))
                                {
                                    return dateTime2;
                                }
                                else
                                {
                                    return null;
                                }
                            }
                        }
                        else
                        {
                            return null;
                        }
                    }
                default:
                    {
                        return propertyValue.GetString();
                    }
            }
        }

    }
}