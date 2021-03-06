﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using WebMarkupMin.Core.Resources;
using WebMarkupMin.Core.Utilities;

namespace WebMarkupMin.Core.Parsers
{
	/// <summary>
	/// XML parser
	/// </summary>
	internal sealed class XmlParser : MarkupParserBase
	{
		#region Regular expressions for parsing tags and attributes

		private const string NAME_PATTERN = @"[\w-:.]+";

		private static readonly Regex _processingInstructionRegex =
			new Regex(@"^<\?(?<instructionName>" + NAME_PATTERN + ")" +
				@"(\s+(?<attributes>(\s*" + NAME_PATTERN + @"\s*=\s*((?:""[^""]*?"")|(?:'[^']*?')))+))?" +
				@"\s*\?>");
		private static readonly Regex _startTagRegex =
			new Regex(@"^<(?<tagName>" + NAME_PATTERN + ")" +
				@"(\s+(?<attributes>(\s*" + NAME_PATTERN + @"\s*=\s*((?:""[^""]*?"")|(?:'[^']*?')))+))?" +
				@"\s*(?<emptyTagSlash>/?)>");
		private static readonly Regex _endTagRegex = new Regex(@"^<\/(?<tagName>" + NAME_PATTERN + @")\s*>");
		private static readonly Regex _attributeRegex = new Regex(@"(?<attributeName>" + NAME_PATTERN + ")" +
			@"\s*=\s*" +
			@"(""(?<attributeValue>[^""]*?)""|'(?<attributeValue>[^']*?)')");

		#endregion

		/// <summary>
		/// XML parsing handlers
		/// </summary>
		private readonly XmlParsingHandlers _handlers;

		/// <summary>
		/// Stack of tags
		/// </summary>
		private readonly Stack<StackedXmlTag> _tagStack;

		/// <summary>
		/// Gets a common markup parsing handlers
		/// </summary>
		protected override MarkupParsingHandlersBase CommonHandlers
		{
			get { return _handlers; }
		}


		/// <summary>
		/// Constructs instance of XML parser
		/// </summary>
		/// <param name="handlers">XML parsing handlers</param>
		public XmlParser(XmlParsingHandlers handlers)
		{
			_handlers = handlers;
			_tagStack = new Stack<StackedXmlTag>();
		}


		/// <summary>
		/// Parses XML content
		/// </summary>
		/// <param name="content">XML content</param>
		public void Parse(string content)
		{
			int contentLength = content.Length;
			if (contentLength == 0)
			{
				return;
			}

			lock (_parsingSynchronizer)
			{
				_innerContext = new InnerMarkupParsingContext(content);
				_context = new MarkupParsingContext(_innerContext);

				int endPosition = contentLength - 1;
				int previousPosition = -1;

				try
				{
					while (_innerContext.Position <= endPosition)
					{
						bool isProcessed = false;
						int firstCharPosition = _innerContext.Position;
						char firstCharValue;
						bool firstCharExist = content.TryGetChar(firstCharPosition, out firstCharValue);

						if (firstCharExist && firstCharValue == '<')
						{
							int secondCharPosition = firstCharPosition + 1;
							char secondCharValue;
							bool secondCharExist = content.TryGetChar(secondCharPosition, out secondCharValue);

							if (secondCharExist)
							{
								if (IsTagFirstChar(secondCharValue))
								{
									// Start tag
									isProcessed = ProcessStartTag();
								}
								else
								{
									int thirdCharPosition = secondCharPosition + 1;
									char thirdCharValue;
									bool thirdCharExist = content.TryGetChar(thirdCharPosition, out thirdCharValue);

									if (thirdCharExist)
									{
										switch (secondCharValue)
										{
											case '/':
												if (IsTagFirstChar(thirdCharValue))
												{
													// End tag
													isProcessed = ProcessEndTag();
												}
												break;

											case '!':
												switch (thirdCharValue)
												{
													case '-':
														int fourthCharPosition = thirdCharPosition + 1;
														char fourthCharValue;
														bool fourthCharExist = content.TryGetChar(
															fourthCharPosition, out fourthCharValue);

														if (fourthCharExist && fourthCharValue == '-')
														{
															// XML comments
															isProcessed = ProcessComment();
														}
														break;

													case '[':
														// CDATA sections
														isProcessed = ProcessCdataSection();
														break;

													case 'D':
													case 'd':
														// Doctype declaration
														isProcessed = ProcessDoctype();
														break;
												}
												break;

											case '?':
												// XML declaration and processing instructions
												isProcessed = ProcessProcessingInstruction();
												break;
										}
									}
								}
							}
						}

						if (!isProcessed)
						{
							// Text
							ProcessText();
						}

						if (_innerContext.Position == previousPosition)
						{
							throw new MarkupParsingException(
								string.Format(Strings.ErrorMessage_MarkupParsingFailed, "XML"),
								_innerContext.NodeCoordinates, _innerContext.GetSourceFragment());
						}

						previousPosition = _innerContext.Position;
					}

					// Check whether there were not closed tags
					if (_tagStack.Count > 0)
					{
						StackedXmlTag stackedTag = _tagStack.Pop();

						throw new MarkupParsingException(
							string.Format(Strings.ErrorMessage_NotClosedTag, stackedTag.Name),
							stackedTag.Coordinates,
							SourceCodeNavigator.GetSourceFragment(_innerContext.SourceCode, stackedTag.Coordinates));
					}
				}
				catch (MarkupParsingException)
				{
					throw;
				}
				finally
				{
					_tagStack.Clear();

					_context = null;
					_innerContext = null;
				}
			}
		}

		#region Processing methods

		/// <summary>
		/// Process a XML declaration and processing instructions
		/// </summary>
		/// <returns>Result of processing (true - is processed; false - is not processed)</returns>
		private bool ProcessProcessingInstruction()
		{
			bool isProcessed = false;
			string content = _innerContext.SourceCode;
			int contentRemainderLength = _innerContext.RemainderLength;

			var match = _processingInstructionRegex.Match(content, _innerContext.Position, contentRemainderLength);
			if (match.Success)
			{
				GroupCollection groups = match.Groups;

				string instruction = match.Value;
				string instructionName = groups["instructionName"].Value;
				string attributesString = groups["attributes"].Value;

				IList<XmlAttribute> attributes = ParseAttributes(attributesString);
				if (String.Equals(instructionName, "xml", StringComparison.OrdinalIgnoreCase))
				{
					if (_handlers.XmlDeclaration != null)
					{
						_handlers.XmlDeclaration(_context, attributes);
					}
				}
				else
				{
					if (_handlers.ProcessingInstruction != null)
					{
						_handlers.ProcessingInstruction(_context, instructionName, attributes);
					}
				}

				_innerContext.IncreasePosition(instruction.Length);
				isProcessed = true;
			}

			return isProcessed;
		}

		/// <summary>
		/// Process a start tag
		/// </summary>
		/// <returns>Result of processing (true - is processed; false - is not processed)</returns>
		private bool ProcessStartTag()
		{
			bool isProcessed = false;
			string content = _innerContext.SourceCode;
			int contentRemainderLength = _innerContext.RemainderLength;

			var match = _startTagRegex.Match(content, _innerContext.Position, contentRemainderLength);
			if (match.Success)
			{
				string startTag = match.Value;

				GroupCollection groups = match.Groups;
				string startTagName = groups["tagName"].Value;
				string attributesString = groups["attributes"].Value;
				bool isEmptyTag = (groups["emptyTagSlash"].Value.Length > 0);

				IList<XmlAttribute> attributes = ParseAttributes(attributesString);
				if (isEmptyTag)
				{
					if (_handlers.EmptyTag != null)
					{
						_handlers.EmptyTag(_context, startTagName, attributes);
					}
				}
				else
				{
					_tagStack.Push(new StackedXmlTag(startTagName, _innerContext.NodeCoordinates));

					if (_handlers.StartTag != null)
					{
						_handlers.StartTag(_context, startTagName, attributes);
					}
				}

				_innerContext.IncreasePosition(startTag.Length);
				isProcessed = true;
			}

			return isProcessed;
		}

		/// <summary>
		/// Process a end tag
		/// </summary>
		/// <returns>Result of processing (true - is processed; false - is not processed)</returns>
		private bool ProcessEndTag()
		{
			bool isProcessed = false;
			string content = _innerContext.SourceCode;
			int contentRemainderLength = _innerContext.RemainderLength;

			var match = _endTagRegex.Match(content, _innerContext.Position, contentRemainderLength);
			if (match.Success)
			{
				string endTag = match.Value;
				string endTagName = match.Groups["tagName"].Value;

				if (_tagStack.Count == 0)
				{
					throw new MarkupParsingException(
						string.Format(Strings.ErrorMessage_StartTagNotDeclared, endTagName),
						_innerContext.NodeCoordinates, _innerContext.GetSourceFragment());
				}

				StackedXmlTag stackedTag = _tagStack.Pop();
				if (stackedTag.Name != endTagName)
				{
					if (_tagStack.Any(t => t.Name == endTagName))
					{
						throw new MarkupParsingException(
							string.Format(Strings.ErrorMessage_NotClosedTag, stackedTag.Name),
							stackedTag.Coordinates,
							SourceCodeNavigator.GetSourceFragment(_innerContext.SourceCode, stackedTag.Coordinates));
					}

					throw new MarkupParsingException(
						string.Format(Strings.ErrorMessage_StartTagNotDeclared, endTagName),
						_innerContext.NodeCoordinates, _innerContext.GetSourceFragment());
				}

				if (_handlers.EndTag != null)
				{
					_handlers.EndTag(_context, endTagName);
				}

				_innerContext.IncreasePosition(endTag.Length);
				isProcessed = true;
			}

			return isProcessed;
		}

		/// <summary>
		/// Process a text
		/// </summary>
		private void ProcessText()
		{
			string content = _innerContext.SourceCode;

			string text;
			int tagPosition = content.IndexOf("<", _innerContext.Position, StringComparison.Ordinal);

			if (tagPosition >= _innerContext.Position)
			{
				text = content.Substring(_innerContext.Position, tagPosition - _innerContext.Position);
			}
			else
			{
				text = content.Substring(_innerContext.Position);
			}

			if (_handlers.Text != null)
			{
				_handlers.Text(_context, text);
			}

			_innerContext.IncreasePosition(text.Length);
		}

		#endregion

		#region Parsing methods

		/// <summary>
		/// Parses a attributes
		/// </summary>
		/// <param name="attributesString">String representation of the attribute list</param>
		/// <returns>List of attributes</returns>
		private IList<XmlAttribute> ParseAttributes(string attributesString)
		{
			var attributes = new List<XmlAttribute>();
			if (string.IsNullOrWhiteSpace(attributesString))
			{
				return attributes;
			}

			MatchCollection attributeMatches = _attributeRegex.Matches(attributesString);

			foreach (Match attributeMatch in attributeMatches)
			{
				GroupCollection groups = attributeMatch.Groups;

				string attributeName = groups["attributeName"].Value;
				string attributeValue = groups["attributeValue"].Value;
				if (!string.IsNullOrWhiteSpace(attributeValue))
				{
					attributeValue = XmlAttribute.XmlAttributeDecode(attributeValue);
				}

				attributes.Add(new XmlAttribute(attributeName, attributeValue));
			}

			return attributes;
		}

		#endregion

		#region Determining methods

		/// <summary>
		/// Checks whether the character is valid first character of XML tag name
		/// </summary>
		/// <param name="value">Character value</param>
		/// <returns>Result of check (true - valid; false - not valid)</returns>
		private static bool IsTagFirstChar(char value)
		{
			return char.IsLetter(value) || value == '_';
		}

		#endregion

		#region Internal types

		/// <summary>
		/// Stacked XML tag
		/// </summary>
		private sealed class StackedXmlTag
		{
			/// <summary>
			/// Name
			/// </summary>
			public string Name
			{
				get;
				private set;
			}

			/// <summary>
			/// Coordinates of tag
			/// </summary>
			public SourceCodeNodeCoordinates Coordinates
			{
				get;
				private set;
			}


			/// <summary>
			/// Constructs instance of stacked XML tag
			/// </summary>
			/// <param name="name">Name</param>
			/// <param name="coordinates">Coordinates of tag</param>
			public StackedXmlTag(string name, SourceCodeNodeCoordinates coordinates)
			{
				Name = name;
				Coordinates = coordinates;
			}
		}

		#endregion
	}
}