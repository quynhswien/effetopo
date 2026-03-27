using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace effetopo.Services
{
    /// <summary>
    /// Service providing utility methods for Revit API operations
    /// </summary>
    public class RevitUtilitiesService
    {
        private static RevitUtilitiesService _instance;
        private static readonly object _lock = new object();

        private RevitUtilitiesService() { }

        public static RevitUtilitiesService Instance
        {
            get
            {
                if (_instance == null)
                {
                    lock (_lock)
                    {
                        if (_instance == null)
                        {
                            _instance = new RevitUtilitiesService();
                        }
                    }
                }
                return _instance;
            }
        }

        /// <summary>
        /// Creates a shared parameter or validates an existing one
        /// </summary>
        /// <param name="app">The Revit application</param>
        /// <param name="doc">The current Revit document</param>
        /// <param name="parameterName">Name of the parameter</param>
        /// <param name="parameterType">Type of the parameter</param>
        /// <param name="groupName">Name of the parameter group</param>
        /// <param name="categoryId">Category ID to bind parameter to</param>
        /// <param name="instance">True for instance parameter, false for type parameter</param>
        /// <param name="userModifiable">True if parameter can be modified by user</param>
        /// <param name="visible">True if parameter should be visible</param>
        /// <returns>The binding GUID for the parameter</returns>
        public Guid CreateSharedParameter(
            Autodesk.Revit.ApplicationServices.Application app,
            Document doc,
            string parameterName,
            ForgeTypeId parameterType,
            string groupName,
            ElementId categoryId,
            bool instance = true,
            bool userModifiable = true,
            bool visible = true)
        {
            return CreateSharedParameterInternal(app, doc, parameterName, parameterType, groupName,
                GetCategory(doc, categoryId), instance, userModifiable, visible);
        }

        /// <summary>
        /// Creates a shared parameter or validates an existing one
        /// </summary>
        /// <param name="app">The Revit application</param>
        /// <param name="doc">The current Revit document</param>
        /// <param name="parameterName">Name of the parameter</param>
        /// <param name="parameterType">Type of the parameter</param>
        /// <param name="groupName">Name of the parameter group</param>
        /// <param name="builtInCategory">Built-in category to bind parameter to</param>
        /// <param name="instance">True for instance parameter, false for type parameter</param>
        /// <param name="userModifiable">True if parameter can be modified by user</param>
        /// <param name="visible">True if parameter should be visible</param>
        /// <returns>The binding GUID for the parameter</returns>
        public Guid CreateSharedParameter(
            Autodesk.Revit.ApplicationServices.Application app,
            Document doc,
            string parameterName,
            ForgeTypeId parameterType,
            string groupName,
            BuiltInCategory builtInCategory,
            bool instance = true,
            bool userModifiable = true,
            bool visible = true)
        {
            return CreateSharedParameterInternal(app, doc, parameterName, parameterType, groupName,
                GetCategory(doc, builtInCategory), instance, userModifiable, visible);
        }

        private Category GetCategory(Document doc, ElementId categoryId)
        {
            Category category = Category.GetCategory(doc, categoryId);
            if (category == null)
            {
#if REVIT2024_OR_GREATER
                throw new ArgumentException($"Invalid category ID: {categoryId.Value}");
#else
                throw new ArgumentException($"Invalid category ID: {categoryId.IntegerValue}");
#endif
            }
            return category;
        }

        private Category GetCategory(Document doc, BuiltInCategory builtInCategory)
        {
            Category category = Category.GetCategory(doc, builtInCategory);
            if (category == null)
            {
                throw new ArgumentException($"Invalid built-in category: {builtInCategory}");
            }
            return category;
        }

        private Guid CreateSharedParameterInternal(
            Autodesk.Revit.ApplicationServices.Application app,
            Document doc,
            string parameterName,
            ForgeTypeId parameterType,
            string groupName,
            Category category,
            bool instance,
            bool userModifiable,
            bool visible)
        {
            try
            {
                // Check if the parameter already exists in the category
                var collector = new FilteredElementCollector(doc)
                    .OfCategoryId(category.Id)
                    .WhereElementIsNotElementType();

                Element sampleElement = collector.FirstElement();
                if (sampleElement != null)
                {
                    // Check the element for the parameter
                    Parameter existingParam = sampleElement.LookupParameter(parameterName);
                    if (existingParam != null)
                    {
                        // Parameter exists, verify its type
                        var existingParamDef = existingParam.Definition;
                        if (existingParamDef != null)
                        {
                            // Check if it's a shared parameter
                            if (existingParamDef is ExternalDefinition extDef)
                            {
                                // Check parameter type compatibility
                                if (extDef.GetDataType() != parameterType)
                                {
                                    throw new InvalidOperationException(
                                        $"Parameter '{parameterName}' already exists but with different type. " +
                                        $"Existing: {extDef.GetDataType()}, Requested: {parameterType}");
                                }
                                return extDef.GUID;
                            }
                            else
                            {
                                // Parameter exists but is not a shared parameter
                                throw new InvalidOperationException(
                                    $"A non-shared parameter named '{parameterName}' already exists on category {category.Name}");
                            }
                        }
                    }
                }

                // Get the binding map to check if parameter already exists in document
                BindingMap bindingMap = doc.ParameterBindings;
                DefinitionBindingMapIterator it = bindingMap.ForwardIterator();
                it.Reset();

                // Check if parameter already exists in document
                while (it.MoveNext())
                {
                    Definition def = it.Key;
                    if (def.Name.Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Parameter with this name already exists
                        // Check if it's of the correct type
                        ExternalDefinition extDef = def as ExternalDefinition;
                        if (extDef != null)
                        {
                            // Check parameter type compatibility
                            if (extDef.GetDataType() != parameterType)
                            {
                                throw new InvalidOperationException(
                                    $"Parameter '{parameterName}' already exists but with different type. " +
                                    $"Existing: {extDef.GetDataType()}, Requested: {parameterType}");
                            }

                            // Get the current binding
                            var currentBinding = it.Current;

                            // Check if correct binding type (instance vs type)
                            bool isCurrentInstance = currentBinding is InstanceBinding;

                            if (isCurrentInstance != instance)
                            {
                                // Parameter exists but with wrong binding type
                                throw new InvalidOperationException(
                                    $"Parameter '{parameterName}' already exists but with " +
                                    $"different binding type. Existing: {(isCurrentInstance ? "Instance" : "Type")}, " +
                                    $"Requested: {(instance ? "Instance" : "Type")}");
                            }

                            // Check if this binding includes our category
                            ElementBinding elemBinding = currentBinding as ElementBinding;
                            if (elemBinding != null)
                            {
                                CategorySet boundCategories = elemBinding.Categories;
                                foreach (Category boundCategory in boundCategories)
                                {
#if REVIT2024_OR_GREATER
                                    if (boundCategory.Id.Value == category.Id.Value)
#else
                                    if (boundCategory.Id.IntegerValue == category.Id.IntegerValue)
#endif
                                    {
                                        // This parameter is already bound to our category
                                        return extDef.GUID;
                                    }
                                }

                                // Parameter exists but not bound to our category
                                // We'll bind it to our category below
                            }
                        }
                    }
                }

                // Store original shared parameters file
                string originalSharedParamFile = app.SharedParametersFilename;
                string tempSharedParamFile = null;

                try
                {
                    // Create temporary file for shared parameters
                    tempSharedParamFile = Path.GetTempFileName();
                    app.SharedParametersFilename = tempSharedParamFile;

                    // Get shared parameter file handle
                    DefinitionFile defFile = app.OpenSharedParameterFile();
                    if (defFile == null)
                    {
                        throw new InvalidOperationException("Failed to open shared parameter file");
                    }

                    // Find or create parameter group
                    DefinitionGroup defGroup = defFile.Groups.get_Item(groupName);
                    if (defGroup == null)
                    {
                        defGroup = defFile.Groups.Create(groupName);
                    }

                    // Create a new shared parameter definition
                    ExternalDefinitionCreationOptions options = new ExternalDefinitionCreationOptions(
                        parameterName,
                        parameterType)
                    {
                        HideWhenNoValue = false,
                        UserModifiable = userModifiable,
                        Visible = visible
                    };

                    ExternalDefinition extDefinition = defGroup.Definitions.Create(options) as ExternalDefinition;
                    if (extDefinition == null)
                    {
                        throw new InvalidOperationException($"Failed to create parameter definition '{parameterName}'");
                    }

                    // Create category set for binding
                    CategorySet cats = app.Create.NewCategorySet();
                    cats.Insert(category);

                    // Create binding
                    Binding newBinding;
                    if (instance)
                    {
                        newBinding = app.Create.NewInstanceBinding(cats);
                    }
                    else
                    {
                        newBinding = app.Create.NewTypeBinding(cats);
                    }

                    // Add the binding
                    using (Transaction tx = new Transaction(doc, $"Add Shared Parameter {parameterName}"))
                    {
                        tx.Start();
                        bindingMap.Insert(extDefinition, newBinding);
                        tx.Commit();
                    }

                    // Record usage statistic
                    if (StatisticsCollectorService.Instance != null)
                    {
                        StatisticsCollectorService.Instance.RecordFeatureUsage("CreateSharedParameter", 1,
                            new Dictionary<string, object> {
                                { "parameterName", parameterName },
                                { "parameterType", parameterType.ToString() },
                                { "categoryName", category.Name }
                            });
                    }

                    return extDefinition.GUID;
                }
                finally
                {
                    // Restore original shared parameters file
                    app.SharedParametersFilename = originalSharedParamFile;

                    // Clean up temp file
                    if (tempSharedParamFile != null && File.Exists(tempSharedParamFile))
                    {
                        try
                        {
                            File.Delete(tempSharedParamFile);
                        }
                        catch
                        {
                            // Ignore errors deleting temp file
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Record error
                if (StatisticsCollectorService.Instance != null)
                {
                    StatisticsCollectorService.Instance.RecordError(
                        "SharedParameterCreationError",
                        ex.Message,
                        ex.StackTrace,
                        new Dictionary<string, object> {
                            { "parameterName", parameterName },
#if REVIT2024_OR_GREATER
                            { "categoryId", category.Id.Value.ToString() }
#else
                            { "categoryId", category.Id.IntegerValue.ToString() }
#endif
                        });
                }
                throw;
            }
        }

        /// <summary>
        /// Gets the value of a parameter from an element
        /// </summary>
        /// <typeparam name="T">The expected return type</typeparam>
        /// <param name="element">The element to get the parameter from</param>
        /// <param name="parameterName">The name of the parameter</param>
        /// <param name="defaultValue">Default value to return if parameter doesn't exist or can't be converted</param>
        /// <returns>The parameter value or default</returns>
        public T GetParameterValue<T>(Element element, string parameterName, T defaultValue = default)
        {
            try
            {
                if (element == null)
                {
                    return defaultValue;
                }

                // Try to get parameter by name
                Parameter param = element.LookupParameter(parameterName);
                if (param == null)
                {
                    // Try getting from element type
                    ElementId typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        Element typeElement = element.Document.GetElement(typeId);
                        param = typeElement?.LookupParameter(parameterName);
                    }

                    if (param == null)
                    {
                        return defaultValue;
                    }
                }

                // Extract the value based on storage type
                object value = null;

                switch (param.StorageType)
                {
                    case StorageType.Integer:
                        value = param.AsInteger();
                        break;
                    case StorageType.Double:
                        value = param.AsDouble();
                        break;
                    case StorageType.String:
                        value = param.AsString();
                        break;
                    case StorageType.ElementId:
                        value = param.AsElementId();
                        break;
                    default:
                        return defaultValue;
                }

                // Try to convert to the requested type
                if (value != null)
                {
                    try
                    {
                        // Handle common type conversions
                        if (typeof(T) == typeof(double) && value is int intValue)
                        {
                            return (T)(object)((double)intValue);
                        }
                        else if (typeof(T) == typeof(int) && value is double doubleValue)
                        {
                            return (T)(object)((int)doubleValue);
                        }
                        else if (typeof(T) == typeof(bool) && value is int boolIntValue)
                        {
                            return (T)(object)(boolIntValue != 0);
                        }
                        else if (typeof(T) == typeof(ElementId) && value is ElementId elementId)
                        {
                            return (T)value;
                        }
                        else
                        {
                            // Try direct conversion
                            return (T)Convert.ChangeType(value, typeof(T));
                        }
                    }
                    catch
                    {
                        return defaultValue;
                    }
                }
            }
            catch (Exception ex)
            {
                // Record error
                if (StatisticsCollectorService.Instance != null)
                {
                    StatisticsCollectorService.Instance.RecordError(
                        "ParameterValueReadError",
                        ex.Message,
                        ex.StackTrace,
                        new Dictionary<string, object> {
                            { "parameterName", parameterName },
#if REVIT2024_OR_GREATER
                            { "elementId", element?.Id?.Value.ToString() ?? "null" }
#else
                            { "elementId", element?.Id?.IntegerValue.ToString() ?? "null" }
#endif
                        });
                }
            }

            return defaultValue;
        }

        /// <summary>
        /// Sets the value of a parameter on an element
        /// </summary>
        /// <typeparam name="T">The type of the value to set</typeparam>
        /// <param name="element">The element to set the parameter on</param>
        /// <param name="parameterName">The name of the parameter</param>
        /// <param name="value">The value to set</param>
        /// <param name="transaction">Optional transaction to use (if null, a new transaction will be created)</param>
        /// <returns>True if parameter was set successfully</returns>
        public bool SetParameterValue<T>(Element element, string parameterName, T value, Transaction transaction = null)
        {
            bool startedTransaction = false;
            Transaction tx = transaction;

            try
            {
                if (element == null)
                {
                    return false;
                }

                // Get the parameter
                Parameter param = element.LookupParameter(parameterName);
                if (param == null)
                {
                    return false;
                }

                // Check if parameter is read-only
                if (param.IsReadOnly)
                {
                    return false;
                }

                // Start a transaction if one wasn't provided
                if (tx == null)
                {
                    tx = new Transaction(element.Document, $"Set Parameter {parameterName}");
                    tx.Start();
                    startedTransaction = true;
                }

                // Set the value based on storage type
                bool result = false;
                switch (param.StorageType)
                {
                    case StorageType.Integer:
                        if (value is bool boolValue)
                        {
                            result = param.Set(boolValue ? 1 : 0);
                        }
                        else
                        {
                            result = param.Set(Convert.ToInt32(value));
                        }
                        break;
                    case StorageType.Double:
                        result = param.Set(Convert.ToDouble(value));
                        break;
                    case StorageType.String:
                        result = param.Set(value.ToString());
                        break;
                    case StorageType.ElementId:
                        if (value is ElementId idValue)
                        {
                            result = param.Set(idValue);
                        }
                        else if (value is long longValue)
                        {
#if REVIT2024_OR_GREATER
                            result = param.Set(new ElementId(longValue));
#else
                            result = param.Set(new ElementId((int)longValue));
#endif
                        }
                        break;
                }

                // Commit transaction if we started it
                if (startedTransaction && tx.GetStatus() == TransactionStatus.Started)
                {
                    tx.Commit();
                }

                // Record usage statistic
                if (StatisticsCollectorService.Instance != null)
                {
                    StatisticsCollectorService.Instance.RecordFeatureUsage("SetParameterValue", 1,
                        new Dictionary<string, object> {
                            { "parameterName", parameterName },
                            { "success", result }
                        });
                }

                return result;
            }
            catch (Exception ex)
            {
                // Roll back transaction if we started it
                if (startedTransaction && tx?.GetStatus() == TransactionStatus.Started)
                {
                    tx.RollBack();
                }

                // Record error
                if (StatisticsCollectorService.Instance != null)
                {
                    StatisticsCollectorService.Instance.RecordError(
                        "ParameterValueWriteError",
                        ex.Message,
                        ex.StackTrace,
                        new Dictionary<string, object> {
                            { "parameterName", parameterName },
#if REVIT2024_OR_GREATER
                            { "elementId", element?.Id?.Value.ToString() ?? "null" }
#else
                            { "elementId", element?.Id?.IntegerValue.ToString() ?? "null" }
#endif
                        });
                }
                return false;
            }
        }
    }
}